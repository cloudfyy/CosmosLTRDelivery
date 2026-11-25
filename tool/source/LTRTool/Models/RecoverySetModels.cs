using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CosmosLab.LTRTool;

public sealed record LtrTarget(
    LtrApi Api,
    string AccountName,
    string AccountResourceId,
    Uri DataEndpoint,
    string DatabaseName,
    string[] Resources,
    Uri PrimaryBlobEndpoint,
    Uri? ReplicaBlobEndpoint,
    string BlobContainerName,
    string? ManagedIdentityClientId);

public sealed record LocalRecoveryFile(
    string RelativePath,
    [property: JsonIgnore] string LocalPath,
    long ItemCount,
    long ByteCount,
    string Sha256,
    double RequestCharge);

public sealed record RecoveryFile(
    string Path,
    long Bytes,
    string Sha256,
    long ItemCount,
    double RequestCharge);

public sealed record ChecksumManifest(
    int SchemaVersion,
    string BackupId,
    RecoveryFile[] Files);

public sealed record ChangeFeedPosition(
    string Database,
    string Container,
    string FeedRange,
    string StartContinuationToken,
    string EndContinuationToken,
    long ChangeCount,
    double RequestCharge);

public sealed record SampleQueryValidation(
    string Database,
    string Container,
    string Query,
    long ResultCount,
    double RequestCharge,
    bool Passed);

public sealed record RecoveryValidation(
    bool ItemCountsValid,
    bool ChecksumsValid,
    bool SampleQueriesValid,
    bool ReplicaValidated,
    SampleQueryValidation[] SampleQueries,
    string[] Warnings);

public sealed record ContainerRecoveryMetadata(
    string Database,
    string Container,
    string Api,
    JsonObject ContainerProperties,
    string ChangeFeedMode,
    bool ChangeFeedSealed,
    long ItemCount,
    double RequestCharge);

public sealed record ReplicationValidation(
    bool Required,
    string Status,
    string? Endpoint,
    long FileCount,
    long ByteCount,
    string? LastValidatedUtc);

public sealed record RecoveryManifest(
    int SchemaVersion,
    string BackupId,
    string StartTimeUtc,
    string SealTimeUtc,
    string SourceAccount,
    string SourceApi,
    string SourceRegion,
    string Cloud,
    string SdkName,
    string SdkVersion,
    JsonObject AccountMetadata,
    JsonObject DatabaseMetadata,
    ContainerRecoveryMetadata[] Containers,
    Dictionary<string, long> ItemCounts,
    RecoveryFile[] Files,
    ChangeFeedPosition[] ChangeFeedPositions,
    RecoveryValidation Validation,
    ReplicationValidation Replication,
    string Status);

public sealed record LtrExportResult(
    string BackupId,
    string Status,
    string PrimaryPrefix,
    string SourceAccount,
    string Api,
    long ItemCount,
    long FileCount,
    long ByteCount,
    double RequestCharge,
    string StartedUtc,
    string CompletedUtc,
    string ManifestPath,
    string ChecksumsPath);

public sealed record RecoverySetInventoryItem(
    string BackupId,
    string Status,
    string Month,
    string SourceAccount,
    string Api,
    string SourceRegion,
    string Cloud,
    string StorageContainer,
    string Prefix,
    string ManifestPath,
    string ChecksumsPath,
    string StartTimeUtc,
    string SealTimeUtc,
    long ItemCount,
    long FileCount,
    long ByteCount,
    bool PrimaryValidated,
    ReplicationValidation Replication,
    RecoverySetInventoryFile[] Files,
    RecoverySetInventoryContainer[] Containers);

public sealed record RecoverySetInventoryFile(
    string Path,
    long Bytes,
    string Sha256,
    long ItemCount);

public sealed record RecoverySetInventoryContainer(
    string Container,
    string ChangeFeedMode,
    bool ChangeFeedSealed,
    long ItemCount);

public sealed record CompletedRecoverySet(
    RecoveryManifest Manifest,
    LocalRecoveryFile[] Files,
    ReplicationValidation Replication,
    LtrExportResult Result);

public sealed record LtrInventoryResult(
    string Status,
    string StartedUtc,
    string CompletedUtc,
    RecoverySetInventoryItem[] RecoverySets);

public sealed record FeedRangeExportResult(
    string FeedRangeJson,
    LocalRecoveryFile File);

public sealed record ContainerExportResult(
    string DatabaseName,
    string ContainerName,
    string Api,
    LocalRecoveryFile[] Files,
    long ItemCount,
    double RequestCharge,
    ChangeFeedPosition[] ChangeFeedPositions,
    JsonObject Metadata);

public sealed record TokenBucketLease(double RequestCharge, double DelayMilliseconds);

public sealed record AccountExportResult(
    LtrTarget Target,
    Azure.Core.TokenCredential Credential,
    JsonObject AccountMetadata,
    JsonObject DatabaseMetadata,
    ContainerRecoveryMetadata[] Containers,
    IReadOnlyList<LocalRecoveryFile> Files,
    ChangeFeedPosition[] ChangeFeedPositions,
    SampleQueryValidation[] SampleQueries,
    Dictionary<string, long> ItemCounts,
    string ConsistencyLevel,
    string[] ReadableRegions,
    IReadOnlyList<string> Warnings,
    DateTimeOffset StartedUtc);
