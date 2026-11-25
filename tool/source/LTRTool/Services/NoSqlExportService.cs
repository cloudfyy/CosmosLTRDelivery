using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Azure.Cosmos;
using Serilog;

namespace CosmosLab.LTRTool;

public sealed class NoSqlExportService
{
    private readonly RequestChargeThrottle throttle;
    private readonly CosmosMetadataService metadata;
    private readonly ILogger logger;

    public NoSqlExportService(
        RequestChargeThrottle throttle,
        CosmosMetadataService metadata,
        ILogger logger)
    {
        this.throttle = throttle;
        this.metadata = metadata;
        this.logger = logger.ForContext<NoSqlExportService>();
    }

    public async Task<AccountExportResult> ExportAsync(
        LtrTarget target,
        LtrOptions options,
        TokenCredential credential,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        using var client = new CosmosClient(
            target.DataEndpoint.AbsoluteUri.TrimEnd('/'),
            credential,
            new CosmosClientOptions
            {
                ApplicationName = "CosmosLTRTool-NoSql",
                ConnectionMode = ConnectionMode.Gateway,
                MaxRetryAttemptsOnRateLimitedRequests = 9,
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30),
                UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(),
                PriorityLevel = PriorityLevel.Low
            });
        var account = await metadata.ReadAccountAsync(client, target, cancellationToken);
        var database = await metadata.ReadDatabaseAsync(
            client, target, cancellationToken);
        var files = new List<LocalRecoveryFile>();
        var containers = new List<ContainerRecoveryMetadata>();
        var positions = new List<ChangeFeedPosition>();
        var samples = new List<SampleQueryValidation>();
        var itemCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var warnings = new List<string>
        {
            "NoSQL sealing uses LatestVersion Change Feed; delete operations require All Versions and Deletes and are not captured in this recovery-set format yet."
        };

        WriteJsonFile(workRoot, "metadata/account.json", account.Metadata);
        var accountMetadataFile = await FileIntegrity.CreateRecordAsync(
            "metadata/account.json", workRoot, 1, 0, cancellationToken);
        files.Add(accountMetadataFile);

        var databaseMetadata = new JsonObject
        {
            [target.DatabaseName] = database.Metadata
        };
        WriteJsonFile(workRoot, "metadata/databases.json", databaseMetadata);
        files.Add(await FileIntegrity.CreateRecordAsync(
            "metadata/databases.json", workRoot, 1, database.RequestCharge, cancellationToken));

        foreach (var containerName in target.Resources)
        {
            var containerSnapshot = await metadata.ReadContainerAsync(
                client, target, containerName, cancellationToken);
            WriteJsonFile(
                workRoot,
                $"metadata/containers/{containerName}.json",
                containerSnapshot.Metadata);
            files.Add(await FileIntegrity.CreateRecordAsync(
                $"metadata/containers/{containerName}.json",
                workRoot,
                1,
                containerSnapshot.RequestCharge,
                cancellationToken));

            var result = await ExportContainerAsync(
                client,
                target,
                containerName,
                options,
                workRoot,
                cancellationToken);
            files.AddRange(result.Files);
            positions.AddRange(result.ChangeFeedPositions);
            itemCounts[$"{target.DatabaseName}/{containerName}"] = result.ItemCount;
            containers.Add(new ContainerRecoveryMetadata(
                target.DatabaseName,
                containerName,
                "NoSql",
                containerSnapshot.Metadata,
                result.ChangeFeedPositions.Length == 0 ? "NotRequested" : "LatestVersion",
                options.ChangeFeedCatchUpSeconds > 0,
                result.ItemCount,
                result.RequestCharge));

            samples.Add(await RunSampleQueryAsync(
                client, target.DatabaseName, containerName,
                result.ItemCount, options.SampleQueryCount, cancellationToken));
        }

        return new AccountExportResult(
            target,
            credential,
            account.Metadata,
            database.Metadata,
            containers.ToArray(),
            files,
            positions.ToArray(),
            samples.ToArray(),
            itemCounts,
            account.ConsistencyLevel,
            account.ReadableRegions,
            warnings,
            startedUtc);
    }

    private async Task<ContainerExportResult> ExportContainerAsync(
        CosmosClient client,
        LtrTarget target,
        string containerName,
        LtrOptions options,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var container = client.GetDatabase(target.DatabaseName).GetContainer(containerName);
        var ranges = await container.GetFeedRangesAsync(cancellationToken);
        if (ranges.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cosmos DB returned no Feed Ranges for {target.DatabaseName}/{containerName}.");
        }

        var cursors = new ConcurrentDictionary<string, ChangeFeedCursor>();
        if (options.ChangeFeedCatchUpSeconds > 0)
        {
            await Parallel.ForEachAsync(
                ranges.Select((range, index) => (range, index)),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = options.Parallelism
                },
                async (entry, token) =>
                {
                    var cursor = await CaptureChangeFeedStartAsync(
                        container,
                        entry.range,
                        entry.index,
                        target,
                        containerName,
                        token);
                    cursors[cursor.FeedRangeJson] = cursor;
                });
        }

        var exportTasks = ranges.Select((range, index) =>
            new Func<Task<FeedRangeExportResult>>(() =>
                ExportFeedRangeAsync(
                    container,
                    range,
                    index,
                    target,
                    containerName,
                    options,
                    workRoot,
                    cancellationToken)));
        var rangeResults = await RunConcurrentAsync(exportTasks, options.Parallelism, cancellationToken);

        var files = rangeResults.Select(result => result.File).ToList();
        var changeFeedPositions = new List<ChangeFeedPosition>();
        var catchUpTasks = cursors.Values
            .OrderBy(cursor => cursor.Index)
            .Select(cursor => new Func<Task<ChangeFeedCatchUpResult>>(() =>
                CatchUpChangeFeedAsync(
                    container,
                    cursor,
                    target,
                    containerName,
                    options,
                    workRoot,
                    cancellationToken)));
        var catchUpResults = await RunConcurrentAsync(
            catchUpTasks, options.Parallelism, cancellationToken);
        foreach (var result in catchUpResults)
        {
            files.Add(result.File);
            changeFeedPositions.Add(result.Position);
        }
        var itemCount = rangeResults.Sum(result => result.File.ItemCount);
        var requestCharge = files.Sum(file => file.RequestCharge) +
            catchUpResults.Sum(result => result.Position.RequestCharge);
        logger.Information(
            "Completed NoSQL full export for {Database}/{Container}: {Items} items, {Parts} data parts, {DeltaParts} delta parts, {RU:F2} RU",
            target.DatabaseName, containerName, itemCount, rangeResults.Length,
            catchUpResults.Length, requestCharge);
        return new ContainerExportResult(
            target.DatabaseName,
            containerName,
            "NoSql",
            files.ToArray(),
            itemCount,
            requestCharge,
            [.. changeFeedPositions],
            new JsonObject());
    }

    private async Task<FeedRangeExportResult> ExportFeedRangeAsync(
        Container container,
        FeedRange range,
        int rangeIndex,
        LtrTarget target,
        string containerName,
        LtrOptions options,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var relativePath = $"data/{target.DatabaseName}/{containerName}/part-{rangeIndex + 1:D5}.json.gz";
        var localPath = Path.Combine(workRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        long itemCount = 0;
        double requestCharge = 0;
        await using (var fileStream = File.Create(localPath))
        await using (var gzip = new GZipStream(fileStream, CompressionLevel.SmallestSize, leaveOpen: false))
        await using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            using var iterator = container.GetItemQueryIterator<JsonObject>(
                range,
                new QueryDefinition("SELECT * FROM c"),
                continuationToken: null,
                requestOptions: new QueryRequestOptions
                {
                    MaxItemCount = 500,
                    PopulateIndexMetrics = true,
                    PriorityLevel = PriorityLevel.Low
                });
            while (iterator.HasMoreResults)
            {
                try
                {
                    var page = await iterator.ReadNextAsync(cancellationToken);
                    await throttle.AcquireAsync(page.RequestCharge, cancellationToken);
                    requestCharge += page.RequestCharge;
                    foreach (var item in page)
                    {
                        await writer.WriteLineAsync(item.ToJsonString());
                        itemCount++;
                    }
                }
                catch (CosmosException error)
                {
                    logger.Error(error,
                        "NoSQL Feed Range export failed for {Database}/{Container} range={Range}. Status={Status} ActivityId={ActivityId} RequestCharge={RequestCharge} Diagnostics={Diagnostics}",
                        target.DatabaseName, containerName, rangeIndex + 1,
                        error.StatusCode, error.ActivityId, error.RequestCharge, error.Diagnostics);
                    throw;
                }
            }
        }
        var fileRecord = await FileIntegrity.CreateRecordAsync(
            relativePath, workRoot, itemCount, requestCharge, cancellationToken);
        logger.Information(
            "Exported Feed Range {Range} for {Database}/{Container}: {Items} items, {Bytes} bytes, {RU:F2} RU, sha256={Sha256}",
            rangeIndex + 1, target.DatabaseName, containerName, itemCount, fileRecord.ByteCount,
            requestCharge, fileRecord.Sha256);
        return new FeedRangeExportResult(range.ToJsonString(), fileRecord);
    }

    private async Task<ChangeFeedCursor> CaptureChangeFeedStartAsync(
        Container container,
        FeedRange range,
        int index,
        LtrTarget target,
        string containerName,
        CancellationToken cancellationToken)
    {
        using var iterator = container.GetChangeFeedStreamIterator(
            ChangeFeedStartFrom.Now(range),
            ChangeFeedMode.LatestVersion,
            new ChangeFeedRequestOptions { PageSizeHint = 500 });
        var documents = new List<string>();
        double requestCharge = 0;
        while (true)
        {
            ResponseMessage response;
            try
            {
                response = await iterator.ReadNextAsync(cancellationToken);
            }
            catch (CosmosException error)
            {
                logger.Error(error,
                    "Could not initialize Change Feed cursor for {Database}/{Container} range={Range}. Status={Status} ActivityId={ActivityId} Diagnostics={Diagnostics}",
                    target.DatabaseName, containerName, index + 1,
                    error.StatusCode, error.ActivityId, error.Diagnostics);
                throw;
            }
            using (response)
            {
                var charge = response.Headers.RequestCharge;
                await throttle.AcquireAsync(charge, cancellationToken);
                requestCharge += charge;
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (string.IsNullOrWhiteSpace(response.ContinuationToken))
                    {
                        throw new InvalidDataException(
                            $"Change Feed range {index + 1} did not return a start continuation token.");
                    }
                    return new ChangeFeedCursor(
                        index, range.ToJsonString(), response.ContinuationToken, documents, requestCharge);
                }
                response.EnsureSuccessStatusCode();
                documents.AddRange(await ReadChangeFeedDocumentsAsync(response));
            }
        }
    }

    private async Task<ChangeFeedCatchUpResult> CatchUpChangeFeedAsync(
        Container container,
        ChangeFeedCursor cursor,
        LtrTarget target,
        string containerName,
        LtrOptions options,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var relativePath = $"changefeed/{target.DatabaseName}/{containerName}/delta-{cursor.Index + 1:D5}.json.gz";
        var localPath = Path.Combine(workRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var lines = new List<string>(cursor.InitialDocuments);
        var continuation = cursor.StartContinuationToken;
        var requestCharge = cursor.RequestCharge;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(options.ChangeFeedCatchUpSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var iterator = container.GetChangeFeedStreamIterator(
                ChangeFeedStartFrom.ContinuationToken(continuation),
                ChangeFeedMode.LatestVersion,
                new ChangeFeedRequestOptions { PageSizeHint = 500 });
            ResponseMessage response;
            try
            {
                response = await iterator.ReadNextAsync(cancellationToken);
            }
            catch (CosmosException error)
            {
                logger.Error(error,
                    "Change Feed sealing failed for {Database}/{Container} range={Range}. Status={Status} ActivityId={ActivityId} Diagnostics={Diagnostics}",
                    target.DatabaseName, containerName, cursor.Index + 1,
                    error.StatusCode, error.ActivityId, error.Diagnostics);
                throw;
            }
            using (response)
            {
                var charge = response.Headers.RequestCharge;
                await throttle.AcquireAsync(charge, cancellationToken);
                requestCharge += charge;
                if (!string.IsNullOrWhiteSpace(response.ContinuationToken))
                {
                    continuation = response.ContinuationToken;
                }
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    break;
                }
                response.EnsureSuccessStatusCode();
                lines.AddRange(await ReadChangeFeedDocumentsAsync(response));
            }
        }
        if (DateTimeOffset.UtcNow >= deadline)
        {
            throw new TimeoutException(
                $"Change Feed did not catch up for {target.DatabaseName}/{containerName} range {cursor.Index + 1} within {options.ChangeFeedCatchUpSeconds} seconds.");
        }

        await GzipJsonLines.WriteAsync(localPath, lines, cancellationToken);
        var file = await FileIntegrity.CreateRecordAsync(
            relativePath, workRoot, lines.Count, requestCharge, cancellationToken);
        return new ChangeFeedCatchUpResult(
            file,
            new ChangeFeedPosition(
                target.DatabaseName,
                containerName,
                cursor.FeedRangeJson,
                cursor.StartContinuationToken,
                continuation,
                lines.Count,
                requestCharge));
    }

    private async Task<SampleQueryValidation> RunSampleQueryAsync(
        CosmosClient client,
        string databaseName,
        string containerName,
        long exportedItemCount,
        int sampleCount,
        CancellationToken cancellationToken)
    {
        var query = $"SELECT TOP {sampleCount} c.id FROM c";
        var iterator = client.GetDatabase(databaseName).GetContainer(containerName)
            .GetItemQueryIterator<JsonObject>(query);
        long resultCount = 0;
        double requestCharge = 0;
        while (iterator.HasMoreResults && resultCount < sampleCount)
        {
            FeedResponse<JsonObject> response;
            try
            {
                response = await iterator.ReadNextAsync(cancellationToken);
            }
            catch (CosmosException error)
            {
                logger.Error(error,
                    "Sample query failed for {Database}/{Container}. Status={Status} ActivityId={ActivityId} Diagnostics={Diagnostics}",
                    databaseName, containerName, error.StatusCode, error.ActivityId, error.Diagnostics);
                throw;
            }
            await throttle.AcquireAsync(response.RequestCharge, cancellationToken);
            requestCharge += response.RequestCharge;
            resultCount += response.Count;
        }
        var passed = exportedItemCount == 0 ? resultCount == 0 : resultCount > 0;
        return new SampleQueryValidation(
            databaseName, containerName, query, resultCount, requestCharge, passed);
    }

    private static async Task<IReadOnlyList<string>> ReadChangeFeedDocumentsAsync(
        ResponseMessage response)
    {
        if (response.Content is null)
        {
            return [];
        }
        using var document = await JsonDocument.ParseAsync(response.Content);
        if (!document.RootElement.TryGetProperty("Documents", out var documents) ||
            documents.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Change Feed response did not contain a Documents array.");
        }
        return documents.EnumerateArray()
            .Select(item => item.GetRawText())
            .ToArray();
    }

    private static void WriteJsonFile(string workRoot, string relativePath, JsonNode node)
    {
        var path = Path.Combine(workRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            node.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }),
            new UTF8Encoding(false));
    }

    private static async Task<T[]> RunConcurrentAsync<T>(
        IEnumerable<Func<Task<T>>> operations,
        int parallelism,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(parallelism, parallelism);
        var wrapped = operations.Select(async operation =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await operation();
            }
            finally
            {
                semaphore.Release();
            }
        });
        return await Task.WhenAll(wrapped);
    }
}

public sealed record ChangeFeedCursor(
    int Index,
    string FeedRangeJson,
    string StartContinuationToken,
    IReadOnlyList<string> InitialDocuments,
    double RequestCharge);

public sealed record ChangeFeedCatchUpResult(
    LocalRecoveryFile File,
    ChangeFeedPosition Position);
