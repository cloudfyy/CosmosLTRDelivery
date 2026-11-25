using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Serilog;

namespace CosmosLab.LTRTool;

public sealed class RecoverySetStorage
{
    private const string RootFolder = "cosmos-ltr";
    private readonly BlobServiceClient primary;
    private readonly BlobServiceClient? replica;
    private readonly string containerName;
    private readonly ILogger logger;

    public RecoverySetStorage(
        Uri primaryBlobEndpoint,
        Uri? replicaBlobEndpoint,
        Azure.Core.TokenCredential credential,
        string containerName,
        ILogger logger)
    {
        primary = new BlobServiceClient(primaryBlobEndpoint, credential);
        replica = replicaBlobEndpoint is null ? null : new BlobServiceClient(replicaBlobEndpoint, credential);
        this.containerName = containerName;
        this.logger = logger.ForContext<RecoverySetStorage>();
    }

    public static RecoverySetLocation CreateLocation(string accountName, string backupId, DateTimeOffset startedUtc, string? month)
    {
        var recoveryMonth = string.IsNullOrWhiteSpace(month)
            ? startedUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : month!;
        var expectedSuffix = $"-{recoveryMonth}";
        var normalizedId = backupId;
        if (!normalizedId.EndsWith(expectedSuffix, StringComparison.Ordinal))
        {
            normalizedId = $"{normalizedId}{expectedSuffix}";
        }
        var finalPrefix = $"{RootFolder}/{accountName}/{recoveryMonth}";
        var stagingPrefix = $".staging/{RootFolder}/{accountName}/{recoveryMonth}/{normalizedId}/{Guid.NewGuid():N}";
        return new RecoverySetLocation(
            accountName,
            normalizedId,
            recoveryMonth,
            finalPrefix,
            stagingPrefix,
            $"{finalPrefix}/manifest.json",
            $"{finalPrefix}/checksums.json",
            $"{finalPrefix}/status/COMPLETED");
    }

    public static string GetRecoverySetPrefix(string accountName, string month) =>
        $"{RootFolder}/{accountName}/{month}";

    public async Task<LtrExportResult?> ReadCompletedResultAsync(
        RecoverySetLocation location,
        LtrTarget expectedTarget,
        bool requireReplica,
        TimeSpan replicaTimeout,
        CancellationToken cancellationToken)
    {
        var completed = await ReadCompletedSetAsync(
            location,
            expectedTarget,
            requireReplica,
            replicaTimeout,
            cancellationToken);
        return completed?.Result;
    }

    public async Task<CompletedRecoverySet?> ReadCompletedSetAsync(
        RecoverySetLocation location,
        LtrTarget expectedTarget,
        bool requireReplica,
        TimeSpan replicaTimeout,
        CancellationToken cancellationToken)
    {
        var container = primary.GetBlobContainerClient(containerName);
        var marker = container.GetBlobClient(location.CompletedMarkerPath);
        if (!await marker.ExistsAsync(cancellationToken))
        {
            return null;
        }

        var manifestDownload = await container
            .GetBlobClient(location.ManifestPath)
            .DownloadContentAsync(cancellationToken);
        var manifestBytes = manifestDownload.Value.Content.ToArray();
        var manifest = JsonSerializer.Deserialize<RecoveryManifest>(
            manifestBytes,
            JsonOptions) ?? throw new InvalidDataException(
                $"Existing recovery-set manifest is empty for {location.AccountName}/{location.Month}.");
        ValidateCompletedManifest(manifest, location, expectedTarget);

        var checksumDownload = await container
            .GetBlobClient(location.ChecksumsPath)
            .DownloadContentAsync(cancellationToken);
        var checksumManifest = JsonSerializer.Deserialize<ChecksumManifest>(
            checksumDownload.Value.Content.ToArray(),
            JsonOptions) ?? throw new InvalidDataException(
                "Existing recovery-set checksums manifest is empty.");
        ValidateChecksums(manifest, checksumManifest);

        var markerDownload = await marker.DownloadContentAsync(cancellationToken);
        var markerBytes = markerDownload.Value.Content.ToArray();
        var records = manifest.Files.Select(file =>
            new LocalRecoveryFile(
                file.Path, string.Empty, file.ItemCount, file.Bytes, file.Sha256, file.RequestCharge))
            .ToList();
        records.Add(CreateDownloadedRecord(
            "manifest.json", manifestBytes, 1, 0));
        records.Add(CreateDownloadedRecord(
            "status/COMPLETED", markerBytes, 0, 0));
        await VerifyFilesAsync(
            container, location.FinalPrefix, records, "existing-primary", cancellationToken);

        ReplicationValidation replication;
        if (replica is not null)
        {
            replication = await VerifyReplicaAsync(
                location,
                records,
                requireReplica,
                replicaTimeout,
                cancellationToken);
        }
        else if (requireReplica)
        {
            throw new InvalidOperationException(
                "Replica verification is required but no replica Blob endpoint was configured.");
        }
        else
        {
            replication = new ReplicationValidation(
                false, "NotConfigured", null, 0, 0, null);
        }

        logger.Information(
            "Existing recovery set {BackupId} was revalidated and will be reused",
            location.BackupId);
        var result = new LtrExportResult(
                manifest.BackupId,
                "Completed",
                location.FinalPrefix,
                manifest.SourceAccount,
                manifest.SourceApi,
                manifest.ItemCounts.Values.Sum(),
                records.Count,
                records.Sum(file => file.ByteCount),
                0,
                manifest.StartTimeUtc,
                manifest.SealTimeUtc,
                location.ManifestPath,
                location.ChecksumsPath);
        return new CompletedRecoverySet(
            manifest,
            records.ToArray(),
            replication,
            result);
    }

    public async Task<RecoverySetInventoryItem[]> InventoryAsync(
        LtrTarget target,
        string? month,
        bool includeFiles,
        bool requireReplica,
        TimeSpan replicaTimeout,
        CancellationToken cancellationToken)
    {
        var container = primary.GetBlobContainerClient(containerName);
        var root = $"{RootFolder}/{target.AccountName}/";
        var months = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var blob in container.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: root,
            cancellationToken: cancellationToken))
        {
            if (!blob.Name.EndsWith("/status/COMPLETED", StringComparison.Ordinal))
            {
                continue;
            }
            var relative = blob.Name[root.Length..];
            var candidateMonth = relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (candidateMonth is not null &&
                DateOnly.TryParseExact(
                    candidateMonth,
                    "yyyy-MM",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                months.Add(candidateMonth);
            }
        }

        var selectedMonths = months
            .Where(value => month is null || value == month)
            .OrderByDescending(value => value, StringComparer.Ordinal)
            .ToArray();
        var results = new List<RecoverySetInventoryItem>();
        foreach (var selectedMonth in selectedMonths)
        {
            var prefix = GetRecoverySetPrefix(target.AccountName, selectedMonth);
            var manifestDownload = await container
                .GetBlobClient($"{prefix}/manifest.json")
                .DownloadContentAsync(cancellationToken);
            var manifest = JsonSerializer.Deserialize<RecoveryManifest>(
                manifestDownload.Value.Content.ToArray(),
                JsonOptions) ?? throw new InvalidDataException(
                    $"Recovery-set manifest is empty for {target.AccountName}/{selectedMonth}.");
            var startedUtc = DateTimeOffset.Parse(
                manifest.StartTimeUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
            var location = CreateLocation(
                target.AccountName,
                manifest.BackupId,
                startedUtc,
                selectedMonth);
            var completed = await ReadCompletedSetAsync(
                location,
                target,
                requireReplica,
                replicaTimeout,
                cancellationToken) ?? throw new InvalidDataException(
                    $"Completed marker disappeared while reading {location.BackupId}.");
            results.Add(new RecoverySetInventoryItem(
                completed.Manifest.BackupId,
                completed.Manifest.Status,
                selectedMonth,
                completed.Manifest.SourceAccount,
                completed.Manifest.SourceApi,
                completed.Manifest.SourceRegion,
                completed.Manifest.Cloud,
                target.BlobContainerName,
                location.FinalPrefix,
                location.ManifestPath,
                location.ChecksumsPath,
                completed.Manifest.StartTimeUtc,
                completed.Manifest.SealTimeUtc,
                completed.Result.ItemCount,
                completed.Result.FileCount,
                completed.Result.ByteCount,
                true,
                completed.Replication,
                !includeFiles
                    ? []
                    : completed.Files.Select(file =>
                        new RecoverySetInventoryFile(
                            file.RelativePath,
                            file.ByteCount,
                            file.Sha256,
                            file.ItemCount)).ToArray(),
                completed.Manifest.Containers.Select(container =>
                    new RecoverySetInventoryContainer(
                        container.Container,
                        container.ChangeFeedMode,
                        container.ChangeFeedSealed,
                        container.ItemCount)).ToArray()));
        }
        return [.. results];
    }

    public static bool IsExpiredCompletedMonth(
        string month,
        DateTimeOffset now,
        int retentionMonths)
    {
        if (retentionMonths < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionMonths));
        }
        if (!DateOnly.TryParseExact(
            month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedMonth))
        {
            return false;
        }
        var cutoff = new DateOnly(now.Year, now.Month, 1).AddMonths(1 - retentionMonths);
        return parsedMonth < cutoff;
    }

    public async Task PrepareAsync(RecoverySetLocation location, CancellationToken cancellationToken)
    {
        var container = primary.GetBlobContainerClient(containerName);
        if (await container.GetBlobClient(location.CompletedMarkerPath).ExistsAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"A completed recovery set already exists for {location.AccountName}/{location.Month}. " +
                "Refusing to overwrite it without an explicit retention/cleanup operation.");
        }
        await DeletePrefixAsync(container, location.FinalPrefix, cancellationToken);
        await DeletePrefixAsync(container, location.StagingPrefix, cancellationToken);
        await container.GetBlobClient($"{location.StagingPrefix}/status/STAGING")
            .UploadAsync(
                BinaryData.FromString($"Staging {location.BackupId}{Environment.NewLine}"),
                overwrite: true,
                cancellationToken: cancellationToken);
        logger.Information(
            "Prepared recovery set {BackupId}; staging={StagingPrefix}; final={FinalPrefix}",
            location.BackupId, location.StagingPrefix, location.FinalPrefix);
    }

    public async Task StageFilesAsync(
        RecoverySetLocation location,
        IReadOnlyList<LocalRecoveryFile> files,
        CancellationToken cancellationToken)
    {
        var container = primary.GetBlobContainerClient(containerName);
        foreach (var file in files)
        {
            var blobName = $"{location.StagingPrefix}/{file.RelativePath}";
            await using var stream = File.OpenRead(file.LocalPath);
            await container.GetBlobClient(blobName).UploadAsync(
                stream,
                overwrite: true,
                cancellationToken: cancellationToken);
        }
        await VerifyFilesAsync(
            container, location.StagingPrefix, files, "staging", cancellationToken);
    }

    public async Task PromoteAsync(
        RecoverySetLocation location,
        IReadOnlyList<LocalRecoveryFile> files,
        CancellationToken cancellationToken)
    {
        var container = primary.GetBlobContainerClient(containerName);
        await container.GetBlobClient($"{location.FinalPrefix}/status/STAGING")
            .UploadAsync(
                BinaryData.FromString($"Promoting {location.BackupId}{Environment.NewLine}"),
                overwrite: true,
                cancellationToken: cancellationToken);

        foreach (var file in files)
        {
            var staged = container.GetBlobClient($"{location.StagingPrefix}/{file.RelativePath}");
            var final = container.GetBlobClient($"{location.FinalPrefix}/{file.RelativePath}");
            await using var stream = await staged.OpenReadAsync(cancellationToken: cancellationToken);
            await final.UploadAsync(stream, overwrite: true, cancellationToken: cancellationToken);
        }
        await VerifyFilesAsync(
            container, location.FinalPrefix, files, "primary", cancellationToken);
    }

    public async Task<ReplicationValidation> VerifyReplicaAsync(
        RecoverySetLocation location,
        IReadOnlyList<LocalRecoveryFile> files,
        bool required,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (replica is null)
        {
            if (required)
            {
                throw new InvalidOperationException(
                    "Replica verification is required but no replica Blob endpoint was configured.");
            }
            return new ReplicationValidation(
                false, "NotConfigured", null, 0, 0, null);
        }

        var container = replica.GetBlobContainerClient(containerName);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await VerifyFilesAsync(
                    container, location.FinalPrefix, files, "replica", cancellationToken);
                return new ReplicationValidation(
                    true,
                    "Verified",
                    replica.Uri.AbsoluteUri.TrimEnd('/'),
                    files.Count,
                    files.Sum(file => file.ByteCount),
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }
            catch (Exception error) when (
                error is RequestFailedException requestFailed && requestFailed.Status == 404 ||
                error is InvalidDataException)
            {
                lastError = error;
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (Exception error) when (
                !required && IsReplicaAvailabilityError(error))
            {
                lastError = error;
                logger.Warning(
                    error,
                    "Optional replica verification is unavailable for {Prefix}",
                    location.FinalPrefix);
                return new ReplicationValidation(
                    false,
                    "Unavailable",
                    replica.Uri.AbsoluteUri.TrimEnd('/'),
                    0,
                    0,
                    null);
            }
        }
        if (!required)
        {
            logger.Warning(
                lastError,
                "Optional replica verification timed out for {Prefix}; continuing with primary validation only",
                location.FinalPrefix);
            return new ReplicationValidation(
                false,
                "Unavailable",
                replica.Uri.AbsoluteUri.TrimEnd('/'),
                0,
                0,
                null);
        }
        throw new InvalidDataException(
            "Replica did not contain the validated recovery set before the timeout.",
            lastError);
    }

    public async Task CompleteAsync(
        RecoverySetLocation location,
        string content,
        CancellationToken cancellationToken)
    {
        var container = primary.GetBlobContainerClient(containerName);
        await container.DeleteBlobIfExistsAsync($"{location.FinalPrefix}/status/STAGING", cancellationToken: cancellationToken);
        await container.GetBlobClient(location.CompletedMarkerPath).UploadAsync(
            BinaryData.FromString(content),
            overwrite: false,
            cancellationToken: cancellationToken);
        await DeletePrefixAsync(container, location.StagingPrefix, cancellationToken);
        logger.Information(
            "Recovery set {BackupId} is completed and valid at {Prefix}",
            location.BackupId, location.FinalPrefix);
    }

    public async Task DeleteExpiredRecoverySetsAsync(
        string accountName,
        DateTimeOffset now,
        int retentionMonths,
        CancellationToken cancellationToken)
    {
        var container = primary.GetBlobContainerClient(containerName);
        var root = $"{RootFolder}/{accountName}/";
        var cutoff = new DateOnly(now.Year, now.Month, 1).AddMonths(1 - retentionMonths);
        var completedMonths = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var blob in container.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: root,
            cancellationToken: cancellationToken))
        {
            var relative = blob.Name[root.Length..];
            if (!relative.EndsWith("/status/COMPLETED", StringComparison.Ordinal))
            {
                continue;
            }
            var month = relative.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            if (IsExpiredCompletedMonth(month, now, retentionMonths))
            {
                completedMonths.Add(month);
            }
        }
        foreach (var month in completedMonths.Order(StringComparer.Ordinal))
        {
            logger.Information(
                "Deleting expired recovery set {Account}/{Month}; cutoff month={Cutoff}",
                accountName, month, cutoff.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            await DeletePrefixAsync(
                container, $"{root}{month}", cancellationToken);
        }
    }

    private static async Task DeletePrefixAsync(
        BlobContainerClient container,
        string prefix,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: $"{prefix.TrimEnd('/')}/",
            cancellationToken: cancellationToken))
        {
            names.Add(blob.Name);
        }
        foreach (var name in names)
        {
            await container.DeleteBlobIfExistsAsync(name, cancellationToken: cancellationToken);
        }
    }

    private static async Task VerifyFilesAsync(
        BlobContainerClient container,
        string prefix,
        IReadOnlyList<LocalRecoveryFile> files,
        string location,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            ValidateRelativePath(file.RelativePath);
            var blob = container.GetBlobClient($"{prefix}/{file.RelativePath}");
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            if (properties.Value.ContentLength != file.ByteCount)
            {
                throw new InvalidDataException(
                    $"{location} blob size mismatch for '{file.RelativePath}': " +
                    $"expected {file.ByteCount}, found {properties.Value.ContentLength}.");
            }
            var localHash = file.Sha256;
            var downloadedPath = Path.GetTempFileName();
            try
            {
                await blob.DownloadToAsync(downloadedPath, cancellationToken);
                await using var stream = File.OpenRead(downloadedPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                    .ToLowerInvariant();
                if (!string.Equals(hash, localHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"{location} blob hash mismatch for '{file.RelativePath}': " +
                        $"expected {localHash}, found {hash}.");
                }
            }
            finally
            {
                File.Delete(downloadedPath);
            }
        }
    }

    public static void ValidateCompletedManifest(
            RecoveryManifest manifest,
            RecoverySetLocation location,
            LtrTarget expectedTarget)
        {
            if (manifest.Status != "Completed" ||
                manifest.BackupId != location.BackupId ||
                manifest.SourceAccount != expectedTarget.AccountName ||
                manifest.SourceApi != expectedTarget.Api.ToString())
            {
                throw new InvalidDataException(
                    $"Existing manifest identity/status does not match {expectedTarget.AccountName}/{location.Month}.");
            }
            if (!manifest.Validation.ItemCountsValid ||
                !manifest.Validation.ChecksumsValid ||
                !manifest.Validation.SampleQueriesValid)
            {
                throw new InvalidDataException(
                    $"Existing recovery set {location.BackupId} did not pass local validation.");
            }
            foreach (var file in manifest.Files)
            {
                ValidateRelativePath(file.Path);
                if (file.Bytes < 0 || file.Sha256.Length != 64 ||
                    !file.Sha256.All(character => Uri.IsHexDigit(character)))
                {
                    throw new InvalidDataException(
                        $"Existing recovery-set file metadata is invalid for '{file.Path}'.");
                }
            }
        }

    private static void ValidateChecksums(
            RecoveryManifest manifest,
            ChecksumManifest checksums)
        {
            if (checksums.BackupId != manifest.BackupId)
            {
                throw new InvalidDataException(
                    "Existing checksums manifest has a different backup ID.");
            }
            var manifestFiles = manifest.Files.ToDictionary(
                file => file.Path,
                file => file,
                StringComparer.Ordinal);
            foreach (var checksum in checksums.Files)
            {
                if (!manifestFiles.TryGetValue(checksum.Path, out var manifestFile) ||
                    checksum.Bytes != manifestFile.Bytes ||
                    checksum.ItemCount != manifestFile.ItemCount ||
                    !string.Equals(
                        checksum.Sha256, manifestFile.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Existing checksums manifest does not match manifest entry '{checksum.Path}'.");
                }
            }
        }

    private static LocalRecoveryFile CreateDownloadedRecord(
            string relativePath,
            byte[] content,
            long itemCount,
            double requestCharge) =>
            new(
                relativePath,
                string.Empty,
                itemCount,
                content.LongLength,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                requestCharge);

    public static bool IsReplicaAvailabilityError(Exception error)
    {
        if (error is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            return true;
        }
        if (error is RequestFailedException requestFailed)
        {
            return requestFailed.Status is 0 or 403 or 404 or 503;
        }
        return error.InnerException is not null &&
            IsReplicaAvailabilityError(error.InnerException);
    }

    private static void ValidateRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                relativePath.StartsWith('/') ||
                relativePath.StartsWith('\\') ||
                relativePath.Contains('\\') ||
                relativePath.Split('/', StringSplitOptions.None).Any(part =>
                    part is "" or "." or ".."))
            {
                throw new InvalidDataException(
                    $"Recovery-set file path is unsafe: '{relativePath}'.");
            }
        }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed record RecoverySetLocation(
    string AccountName,
    string BackupId,
    string Month,
    string FinalPrefix,
    string StagingPrefix,
    string ManifestPath,
    string ChecksumsPath,
    string CompletedMarkerPath);
