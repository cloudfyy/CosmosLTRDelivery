using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using CosmosLab.Common;
using Gremlin.Net.Driver;
using Gremlin.Net.Driver.Exceptions;
using Gremlin.Net.Structure.IO.GraphSON;
using Microsoft.Azure.Cosmos;
using Serilog;

namespace CosmosLab.LTRTool;

public sealed class GremlinExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly CosmosMetadataService metadata;
    private readonly RequestChargeThrottle throttle;
    private readonly ILogger logger;

    public GremlinExportService(
        CosmosMetadataService metadata,
        RequestChargeThrottle throttle,
        ILogger logger)
    {
        this.metadata = metadata;
        this.throttle = throttle;
        this.logger = logger.ForContext<GremlinExportService>();
    }

    public async Task<AccountExportResult> ExportAsync(
        LtrTarget target,
        LtrOptions options,
        CloudEnvironment cloud,
        TokenCredential credential,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var key = await cloud.ReadGremlinKeyAsync(credential, target.AccountResourceId);
        var account = await metadata.ReadAccountAsync(
            null, target, cancellationToken);
        var database = await metadata.ReadDatabaseAsync(
            null, target, cancellationToken);
        var files = new List<LocalRecoveryFile>();
        var containers = new List<ContainerRecoveryMetadata>();
        var samples = new List<SampleQueryValidation>();
        var itemCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        const string warning =
            "Gremlin API containers do not expose NoSQL Feed Range/Change Feed sealing; the graph is fully exported by g.V() and g.E().";
        var warnings = new[] { warning };

        WriteJsonFile(workRoot, "metadata/account.json", account.Metadata);
        files.Add(await FileIntegrity.CreateRecordAsync(
            "metadata/account.json", workRoot, 1, 0, cancellationToken));
        var databaseMetadata = new JsonObject
        {
            [target.DatabaseName] = database.Metadata
        };
        WriteJsonFile(workRoot, "metadata/databases.json", databaseMetadata);
        files.Add(await FileIntegrity.CreateRecordAsync(
            "metadata/databases.json", workRoot, 1, database.RequestCharge, cancellationToken));

        foreach (var graphName in target.Resources)
        {
            var containerSnapshot = await metadata.ReadContainerAsync(
                null, target, graphName, cancellationToken);
            containerSnapshot.Metadata["feedRangeExportSupported"] = false;
            containerSnapshot.Metadata["changeFeedSealingSupported"] = false;
            WriteJsonFile(
                workRoot,
                $"metadata/containers/{graphName}.json",
                containerSnapshot.Metadata);
            files.Add(await FileIntegrity.CreateRecordAsync(
                $"metadata/containers/{graphName}.json",
                workRoot,
                1,
                containerSnapshot.RequestCharge,
                cancellationToken));

            var graphResult = await ExportGraphAsync(
                target, key, graphName, options, workRoot, cancellationToken);
            files.AddRange(graphResult.Files);
            itemCounts[$"{target.DatabaseName}/{graphName}"] = graphResult.ItemCount;
            containers.Add(new ContainerRecoveryMetadata(
                target.DatabaseName,
                graphName,
                "Gremlin",
                containerSnapshot.Metadata,
                "NotSupportedForGremlin",
                false,
                graphResult.ItemCount,
                graphResult.RequestCharge));
            samples.Add(graphResult.Sample);
        }

        return new AccountExportResult(
            target,
            credential,
            account.Metadata,
            database.Metadata,
            containers.ToArray(),
            files,
            [],
            samples.ToArray(),
            itemCounts,
            account.ConsistencyLevel,
            account.ReadableRegions,
            warnings,
            startedUtc);
    }

    private async Task<GremlinGraphExportResult> ExportGraphAsync(
        LtrTarget target,
        string key,
        string graphName,
        LtrOptions options,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var server = new GremlinServer(
            target.DataEndpoint.Host,
            443,
            enableSsl: true,
            username: $"/dbs/{target.DatabaseName}/colls/{graphName}",
            password: key);
        using var client = new GremlinClient(
            server,
            new GraphSON2Reader(),
            new GraphSON2Writer(),
            GremlinClient.GraphSON2MimeType,
            new ConnectionPoolSettings { PoolSize = 1 });

        var vertices = await ExportTraversalAsync(
            client,
            "g.V()",
            target,
            graphName,
            "vertices",
            workRoot,
            cancellationToken);
        var edges = await ExportTraversalAsync(
            client,
            "g.E()",
            target,
            graphName,
            "edges",
            workRoot,
            cancellationToken);
        var sampleRows = await SubmitWithRetryAsync<Dictionary<string, object>>(
            client, $"g.V().limit({Math.Max(1, options.SampleQueryCount)})", cancellationToken);
        var itemCount = vertices.ItemCount + edges.ItemCount;
        var samplePassed = itemCount == 0 ? sampleRows.Count == 0 : sampleRows.Count > 0;
        logger.Information(
            "Completed Gremlin full export for {Database}/{Graph}: {Vertices} vertices and {Edges} edges",
            target.DatabaseName, graphName, vertices.ItemCount, edges.ItemCount);
        return new GremlinGraphExportResult(
            [vertices, edges],
            itemCount,
            new SampleQueryValidation(
                target.DatabaseName,
                graphName,
                "g.V().limit(sampleCount)",
                sampleRows.Count,
                ReadRequestCharge(sampleRows),
                samplePassed),
            vertices.RequestCharge +
                edges.RequestCharge +
                ReadRequestCharge(sampleRows));
    }

    private async Task<LocalRecoveryFile> ExportTraversalAsync(
        GremlinClient client,
        string query,
        LtrTarget target,
        string graphName,
        string kind,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var relativePath = $"data/{target.DatabaseName}/{graphName}/{kind}.json.gz";
        var localPath = Path.Combine(workRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var rows = await SubmitWithRetryAsync<Dictionary<string, object>>(
            client, query, cancellationToken);
        var lines = rows.Select(row => JsonSerializer.Serialize(row, JsonOptions)).ToArray();
        await GzipJsonLines.WriteAsync(localPath, lines, cancellationToken);
        var count = lines.LongLength;
        var info = new FileInfo(localPath);
        var hash = await FileIntegrity.ComputeSha256Async(localPath, cancellationToken);
        logger.Information(
            "Exported Gremlin {Kind} for {Database}/{Graph}: {Items} items, sha256={Sha256}",
            kind, target.DatabaseName, graphName, count, hash);
        return new LocalRecoveryFile(
            relativePath, localPath, count, info.Length, hash, ReadRequestCharge(rows));
    }

    private async Task<ResultSet<T>> SubmitWithRetryAsync<T>(
        GremlinClient client,
        string query,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 7;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await client.SubmitAsync<T>(query).WaitAsync(
                    TimeSpan.FromSeconds(60), cancellationToken);
                await throttle.AcquireAsync(ReadRequestCharge(result), cancellationToken);
                return result;
            }
            catch (ResponseException error) when (attempt < maxAttempts && IsTransient(error))
            {
                var delay = GetRetryDelay(error, attempt);
                logger.Warning(error,
                    "Gremlin 429/timeout; retry {Attempt}/{Maximum} in {DelayMs:F0} ms. Status={Status}",
                    attempt, maxAttempts - 1, delay.TotalMilliseconds, error.StatusCode);
                await Task.Delay(delay, cancellationToken);
            }
            catch (TimeoutException) when (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(
                    250 * Math.Pow(2, attempt - 1), 30_000));
                logger.Warning(
                    "Gremlin timed out; retry {Attempt}/{Maximum} in {DelayMs:F0} ms",
                    attempt, maxAttempts - 1, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsTransient(ResponseException error)
    {
        if ((int)error.StatusCode == 429 ||
            error.StatusCode.ToString().Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var text = string.Join(
            " ",
            error.StatusAttributes.Select(attribute => $"{attribute.Key}={attribute.Value}")) +
            " " + error.Message;
        return text.Contains("429", StringComparison.Ordinal) ||
               text.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("RequestRateTooLarge", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan GetRetryDelay(ResponseException error, int attempt)
    {
        var retryAfter = error.StatusAttributes
            .FirstOrDefault(attribute =>
                attribute.Key.Contains("retry", StringComparison.OrdinalIgnoreCase) &&
                attribute.Key.Contains("ms", StringComparison.OrdinalIgnoreCase))
            .Value;
        if (double.TryParse(
                Convert.ToString(retryAfter, CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var milliseconds) &&
            double.IsFinite(milliseconds) &&
            milliseconds is >= 0 and <= 30_000)
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }
        if (retryAfter is not null)
        {
            throw new InvalidDataException(
                "Gremlin returned an invalid or excessive retry-after value.", error);
        }
        return TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, attempt - 1), 30_000));
    }

    private static double ReadRequestCharge<T>(ResultSet<T> result)
    {
        var value = result.StatusAttributes
            .FirstOrDefault(attribute =>
                attribute.Key.Equals("x-ms-request-charge", StringComparison.OrdinalIgnoreCase))
            .Value;
        if (value is null)
        {
            return 0;
        }
        if (!double.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var charge) ||
            !double.IsFinite(charge) ||
            charge < 0)
        {
            throw new InvalidDataException(
                $"Gremlin returned an invalid x-ms-request-charge value: {value}.");
        }
        return charge;
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

}

public sealed record GremlinGraphExportResult(
    LocalRecoveryFile[] Files,
    long ItemCount,
    SampleQueryValidation Sample,
    double RequestCharge);
