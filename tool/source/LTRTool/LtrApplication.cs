using Azure.Core;
using CosmosLab.Common;
using Serilog;

namespace CosmosLab.LTRTool;

public sealed class LtrApplication : IAsyncDisposable
{
    private readonly LtrOptions options;
    private readonly ILogger logger;

    public LtrApplication(LtrOptions options, ILogger logger)
    {
        this.options = options;
        this.logger = logger.ForContext<LtrApplication>();
    }

    public async Task<LtrApplicationResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var runtime = CreateRuntime();
        var cloud = runtime.Cloud;
        var resolution = runtime.Resolution;
        var credential = runtime.Credential;
        var throttle = new RequestChargeThrottle(options.MaxRuPerSecond);
        var metadata = new CosmosMetadataService(
            new ArmThroughputMetadataService(cloud, credential, logger),
            logger);
        var noSqlExporter = new NoSqlExportService(throttle, metadata, logger);
        var gremlinExporter = new GremlinExportService(metadata, throttle, logger);
        var finalizer = new RecoverySetFinalizer(logger);
        var results = new List<LtrExportResult>();

        Directory.CreateDirectory(options.WorkingDirectory);
        foreach (var target in resolution.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startedForTarget = DateTimeOffset.UtcNow;
            var backupId = options.BackupId ??
                $"cosmos-{target.AccountName}-{startedForTarget:yyyy-MM}";
            var existingStorage = new RecoverySetStorage(
                target.PrimaryBlobEndpoint,
                target.ReplicaBlobEndpoint,
                credential,
                target.BlobContainerName,
                logger);
            var existingLocation = RecoverySetStorage.CreateLocation(
                target.AccountName,
                backupId,
                startedForTarget,
                options.BackupMonth);
            var existing = await existingStorage.ReadCompletedResultAsync(
                existingLocation,
                target,
                options.RequireReplica,
                TimeSpan.FromMinutes(10),
                cancellationToken);
            if (existing is not null)
            {
                results.Add(existing);
                continue;
            }
            var targetWorkRoot = Path.Combine(
                options.WorkingDirectory,
                $"ltr-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}",
                target.AccountName);
            Directory.CreateDirectory(targetWorkRoot);
            AccountExportResult export;
            try
            {
                logger.Information(
                    "Starting LTR full export. Account={Account} Api={Api} Database={Database} " +
                    "Containers={Containers} Parallelism={Parallelism} MaxRuPerSecond={MaxRuPerSecond}",
                    target.AccountName, target.Api, target.DatabaseName,
                    target.Resources, options.Parallelism, options.MaxRuPerSecond);
                export = target.Api switch
                {
                    LtrApi.NoSql => await noSqlExporter.ExportAsync(
                        target, options, credential, targetWorkRoot, cancellationToken),
                    LtrApi.Gremlin => await gremlinExporter.ExportAsync(
                        target, options, cloud, credential, targetWorkRoot, cancellationToken),
                    _ => throw new InvalidOperationException($"Unsupported target API: {target.Api}")
                };
                results.Add(await finalizer.FinalizeAsync(
                    export, options, targetWorkRoot, cancellationToken));
            }
            finally
            {
                if (Directory.Exists(targetWorkRoot))
                {
                    Directory.Delete(targetWorkRoot, recursive: true);
                }
            }
        }

        return new LtrApplicationResult(
            "Completed",
            startedUtc.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            results.ToArray());
    }

    public async Task<LtrInventoryResult> InventoryAsync(
        CancellationToken cancellationToken = default)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var runtime = CreateRuntime();
        var results = new List<RecoverySetInventoryItem>();
        foreach (var target in runtime.Resolution.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storage = new RecoverySetStorage(
                target.PrimaryBlobEndpoint,
                target.ReplicaBlobEndpoint,
                runtime.Credential,
                target.BlobContainerName,
                logger);
            results.AddRange(await storage.InventoryAsync(
                target,
                options.BackupMonth,
                options.Api != LtrApi.Both,
                requireReplica: false,
                TimeSpan.FromMinutes(10),
                cancellationToken));
        }
        return new LtrInventoryResult(
            "Completed",
            startedUtc.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            [.. results]);
    }

    private LtrRuntimeContext CreateRuntime()
    {
        var configuration = LtrConfiguration.Load(options.ConfigurationPath);
        if (!string.Equals(configuration.CloudName, options.CloudName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The configuration cloud '{configuration.CloudName}' does not match '{options.CloudName}'.");
        }
        var cloud = CloudEnvironment.For(options.CloudName);
        var resolution = new LtrTargetResolver().Resolve(options, configuration);
        var credential = cloud.CreateCredential(
            options.ManagedIdentityClientId ?? configuration.Backup?.ManagedIdentityClientId);
        return new LtrRuntimeContext(cloud, resolution, credential);
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

public sealed record LtrApplicationResult(
    string Status,
    string StartedUtc,
    string CompletedUtc,
    LtrExportResult[] RecoverySets);

public sealed record LtrRuntimeContext(
    CloudEnvironment Cloud,
    TargetResolution Resolution,
    TokenCredential Credential);
