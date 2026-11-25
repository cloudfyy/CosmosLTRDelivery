using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace CosmosLab.LTRTool;

public sealed class RecoverySetFinalizer
{
    private readonly ILogger logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public RecoverySetFinalizer(ILogger logger)
    {
        this.logger = logger.ForContext<RecoverySetFinalizer>();
    }

    public async Task<LtrExportResult> FinalizeAsync(
        AccountExportResult export,
        LtrOptions options,
        string workRoot,
        CancellationToken cancellationToken)
    {
        var startedUtc = export.StartedUtc;
        var storage = new RecoverySetStorage(
            export.Target.PrimaryBlobEndpoint,
            export.Target.ReplicaBlobEndpoint,
            export.Credential,
            export.Target.BlobContainerName,
            logger);
        var backupId = options.BackupId ??
            $"cosmos-{export.Target.AccountName}-{startedUtc:yyyy-MM}";
        var location = RecoverySetStorage.CreateLocation(
            export.Target.AccountName, backupId, startedUtc, options.BackupMonth);
        var stagedFiles = export.Files.ToList();

        await RecoverySetValidator.ValidateAsync(
            stagedFiles, export.Containers, cancellationToken);
        var failedSamples = export.SampleQueries.Where(sample => !sample.Passed).ToArray();
        if (failedSamples.Length != 0)
        {
            throw new InvalidDataException(
                $"Sample-query validation failed for {failedSamples.Length} resource(s).");
        }
        var itemCountValid = export.ItemCounts.All(item => item.Value >= 0);
        var checksumValid = stagedFiles.All(file => file.ByteCount >= 0 &&
            file.Sha256.Length == 64);
        if (!itemCountValid || !checksumValid)
        {
            throw new InvalidDataException(
                "Local export validation failed before staging.");
        }

        var checksums = new ChecksumManifest(
            1,
            location.BackupId,
            stagedFiles.Select(ToRecoveryFile).ToArray());
        var checksumsPath = Path.Combine(workRoot, "checksums.json");
        await File.WriteAllTextAsync(
            checksumsPath,
            JsonSerializer.Serialize(checksums, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);
        stagedFiles.Add(await FileIntegrity.CreateRecordAsync(
            "checksums.json", workRoot, 1, 0, cancellationToken));

        var validation = new RecoveryValidation(
            ItemCountsValid: true,
            ChecksumsValid: true,
            SampleQueriesValid: true,
            ReplicaValidated: false,
            SampleQueries: export.SampleQueries,
            Warnings: export.Warnings.ToArray());
        var manifest = BuildManifest(
            options,
            export,
            location,
            startedUtc,
            DateTimeOffset.UtcNow,
            stagedFiles,
            validation,
            new ReplicationValidation(
                options.RequireReplica,
                export.Target.ReplicaBlobEndpoint is null ? "NotConfigured" : "Pending",
                export.Target.ReplicaBlobEndpoint?.AbsoluteUri.TrimEnd('/'),
                0,
                0,
                null),
            status: "Staging");
        var manifestPath = Path.Combine(workRoot, "manifest.json");
        await WriteManifestAsync(manifestPath, manifest, cancellationToken);
        stagedFiles.Add(await FileIntegrity.CreateRecordAsync(
            "manifest.json", workRoot, 1, 0, cancellationToken));

        await storage.PrepareAsync(location, cancellationToken);
        await storage.StageFilesAsync(location, stagedFiles, cancellationToken);
        await storage.PromoteAsync(location, stagedFiles, cancellationToken);

        var replicaValidation = await storage.VerifyReplicaAsync(
            location,
            stagedFiles,
            options.RequireReplica,
            TimeSpan.FromMinutes(10),
            cancellationToken);
        var completedValidation = validation with { ReplicaValidated =
            !options.RequireReplica || replicaValidation.Status == "Verified" };
        var completedManifest = BuildManifest(
            options,
            export,
            location,
            startedUtc,
            DateTimeOffset.UtcNow,
            stagedFiles.Where(file =>
                file.RelativePath is not ("manifest.json")).ToArray(),
            completedValidation,
            replicaValidation,
            status: "Completed");
        await WriteManifestAsync(manifestPath, completedManifest, cancellationToken);
        var finalManifestFile = await FileIntegrity.CreateRecordAsync(
            "manifest.json", workRoot, 1, 0, cancellationToken);
        await storage.StageFilesAsync(location, [finalManifestFile], cancellationToken);
        await storage.PromoteAsync(location, [finalManifestFile], cancellationToken);
        if (export.Target.ReplicaBlobEndpoint is not null)
        {
            replicaValidation = await storage.VerifyReplicaAsync(
                location,
                [finalManifestFile],
                options.RequireReplica,
                TimeSpan.FromMinutes(10),
                cancellationToken);
        }

        await storage.CompleteAsync(
            location,
            $"Completed {location.BackupId} at {DateTimeOffset.UtcNow:O}{Environment.NewLine}",
            cancellationToken);
        await storage.DeleteExpiredRecoverySetsAsync(
            export.Target.AccountName,
            DateTimeOffset.UtcNow,
            options.RetentionMonths,
            cancellationToken);

        var completedFiles = stagedFiles
            .Where(file => file.RelativePath != "manifest.json")
            .Append(finalManifestFile)
            .ToArray();
        logger.Information(
            "Monthly recovery set {BackupId} is complete for {Account}: {Items} items, {Files} files, {Bytes} bytes",
            location.BackupId, export.Target.AccountName,
            export.ItemCounts.Values.Sum(),
            completedFiles.Length,
            completedFiles.Sum(file => file.ByteCount));
        return new LtrExportResult(
            location.BackupId,
            "Completed",
            location.FinalPrefix,
            export.Target.AccountName,
            export.Target.Api.ToString(),
            export.ItemCounts.Values.Sum(),
            completedFiles.Length,
            completedFiles.Sum(file => file.ByteCount),
            export.Files.Sum(file => file.RequestCharge) +
                export.SampleQueries.Sum(sample => sample.RequestCharge),
            startedUtc.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            location.ManifestPath,
            location.ChecksumsPath);
    }

    private static RecoveryManifest BuildManifest(
        LtrOptions options,
        AccountExportResult export,
        RecoverySetLocation location,
        DateTimeOffset startUtc,
        DateTimeOffset sealUtc,
        IReadOnlyList<LocalRecoveryFile> files,
        RecoveryValidation validation,
        ReplicationValidation replication,
        string status)
    {
        var recoveryFiles = files
            .Where(file => file.RelativePath != "manifest.json")
            .Select(ToRecoveryFile)
            .ToArray();
        return new RecoveryManifest(
            1,
            location.BackupId,
            startUtc.ToString("O", CultureInfo.InvariantCulture),
            sealUtc.ToString("O", CultureInfo.InvariantCulture),
            export.Target.AccountName,
            export.Target.Api.ToString(),
            options.SourceRegion ?? export.ReadableRegions.FirstOrDefault() ?? "unknown",
            options.CloudName,
            "Microsoft.Azure.Cosmos",
            typeof(Microsoft.Azure.Cosmos.CosmosClient).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "unknown",
            export.AccountMetadata,
            export.DatabaseMetadata,
            export.Containers,
            export.ItemCounts,
            recoveryFiles,
            export.ChangeFeedPositions,
            validation,
            replication,
            status);
    }

    private static RecoveryFile ToRecoveryFile(LocalRecoveryFile file) => new(
        file.RelativePath,
        file.ByteCount,
        file.Sha256,
        file.ItemCount,
        file.RequestCharge);

    private static async Task WriteManifestAsync(
        string path,
        RecoveryManifest manifest,
        CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);

}
