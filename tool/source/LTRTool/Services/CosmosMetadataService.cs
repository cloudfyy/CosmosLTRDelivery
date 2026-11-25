using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;
using Serilog;

namespace CosmosLab.LTRTool;

public sealed class CosmosMetadataService
{
    private readonly ArmThroughputMetadataService? armThroughput;
    private readonly ILogger logger;

    public CosmosMetadataService(
        ArmThroughputMetadataService? armThroughput,
        ILogger logger)
    {
        this.armThroughput = armThroughput;
        this.logger = logger.ForContext<CosmosMetadataService>();
    }

    public async Task<AccountSnapshot> ReadAccountAsync(
        CosmosClient? client,
        LtrTarget target,
        CancellationToken cancellationToken)
    {
        if (target.Api == LtrApi.Gremlin)
        {
            return await (armThroughput ??
                throw new InvalidOperationException(
                    "ARM metadata service is required for Gremlin accounts."))
                .ReadGremlinAccountAsync(target, cancellationToken);
        }
        ArgumentNullException.ThrowIfNull(client);
        AccountProperties account;
        try
        {
            account = await client.ReadAccountAsync();
        }
        catch (CosmosException error)
        {
            logger.Error(error,
                "Cosmos account metadata read failed. Status={Status} ActivityId={ActivityId} Diagnostics={Diagnostics}",
                error.StatusCode, error.ActivityId, error.Diagnostics);
            throw;
        }

        var metadata = new JsonObject
        {
            ["id"] = account.Id,
            ["resourceId"] = target.AccountResourceId,
            ["endpoint"] = target.DataEndpoint.AbsoluteUri.TrimEnd('/'),
            ["consistency"] = account.Consistency.DefaultConsistencyLevel.ToString(),
            ["writableRegions"] = new JsonArray(
                account.WritableRegions.Select(region => JsonValue.Create(region.Name)).ToArray()),
            ["readableRegions"] = new JsonArray(
                account.ReadableRegions.Select(region => JsonValue.Create(region.Name)).ToArray()),
            ["sdkName"] = "Microsoft.Azure.Cosmos",
            ["sdkVersion"] = typeof(CosmosClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? typeof(CosmosClient).Assembly.GetName().Version?.ToString() ?? "unknown"
        };
        if (account.Consistency.DefaultConsistencyLevel == ConsistencyLevel.BoundedStaleness)
        {
            metadata["maxIntervalInSeconds"] = account.Consistency.MaxStalenessIntervalInSeconds;
        }
        if (account.Consistency.DefaultConsistencyLevel == ConsistencyLevel.BoundedStaleness)
        {
            metadata["maxStalenessPrefix"] = account.Consistency.MaxStalenessPrefix;
        }
        return new AccountSnapshot(
            metadata,
            account.Consistency.DefaultConsistencyLevel.ToString(),
            account.ReadableRegions.Select(region => region.Name).ToArray());
    }

    public async Task<DatabaseSnapshot> ReadDatabaseAsync(
        CosmosClient? client,
        LtrTarget target,
        CancellationToken cancellationToken)
    {
        if (target.Api == LtrApi.Gremlin)
        {
            return await (armThroughput ??
                throw new InvalidOperationException(
                    "ARM metadata service is required for Gremlin databases."))
                .ReadGremlinDatabaseAsync(target, cancellationToken);
        }
        ArgumentNullException.ThrowIfNull(client);
        var databaseName = target.DatabaseName;
        var database = client.GetDatabase(databaseName);
        DatabaseResponse response;
        try
        {
            response = await database.ReadAsync(cancellationToken: cancellationToken);
        }
        catch (CosmosException error)
        {
            logger.Error(error,
                "Cosmos database metadata read failed for {Database}. Status={Status} ActivityId={ActivityId} Diagnostics={Diagnostics}",
                databaseName, error.StatusCode, error.ActivityId, error.Diagnostics);
            throw;
        }

        var throughput = target.Api == LtrApi.NoSql
            ? await ReadArmThroughputAsync(
                () => armThroughput!.ReadDatabaseAsync(target, cancellationToken),
                $"database/{databaseName}")
            : await ReadThroughputAsync(
                () => database.ReadThroughputAsync(
                    new RequestOptions(), cancellationToken: cancellationToken),
                $"database/{databaseName}",
                cancellationToken);
        return new DatabaseSnapshot(
            new JsonObject
            {
                ["id"] = response.Resource.Id,
                ["throughput"] = throughput
            },
            response.RequestCharge);
    }

    public async Task<ContainerSnapshot> ReadContainerAsync(
        CosmosClient? client,
        LtrTarget target,
        string containerName,
        CancellationToken cancellationToken)
    {
        if (target.Api == LtrApi.Gremlin)
        {
            return await (armThroughput ??
                throw new InvalidOperationException(
                    "ARM metadata service is required for Gremlin graphs."))
                .ReadGremlinGraphAsync(target, containerName, cancellationToken);
        }
        ArgumentNullException.ThrowIfNull(client);
        var databaseName = target.DatabaseName;
        var container = client.GetDatabase(databaseName).GetContainer(containerName);
        ContainerResponse response;
        try
        {
            response = await container.ReadContainerAsync(cancellationToken: cancellationToken);
        }
        catch (CosmosException error)
        {
            logger.Error(error,
                "Cosmos container metadata read failed for {Database}/{Container}. Status={Status} ActivityId={ActivityId} Diagnostics={Diagnostics}",
                databaseName, containerName, error.StatusCode, error.ActivityId, error.Diagnostics);
            throw;
        }
        var throughput = target.Api == LtrApi.NoSql
            ? await ReadArmThroughputAsync(
                () => armThroughput!.ReadContainerAsync(
                    target, containerName, cancellationToken),
                $"container/{databaseName}/{containerName}")
            : await ReadThroughputAsync(
                () => container.ReadThroughputAsync(
                    new RequestOptions(), cancellationToken: cancellationToken),
                $"container/{databaseName}/{containerName}",
                cancellationToken);
        var properties = response.Resource;
        var partitionKeyPaths = properties.PartitionKeyPaths ?? [];
        var metadata = new JsonObject
        {
            ["id"] = properties.Id,
            ["partitionKey"] = new JsonObject
            {
                ["paths"] = new JsonArray(
                    partitionKeyPaths.Select(path => JsonValue.Create(path)).ToArray()),
                ["version"] = properties.PartitionKeyDefinitionVersion.ToString(),
                ["kind"] = properties.PartitionKeyPaths?.Count > 1 ? "MultiHash" : "Hash"
            },
            ["indexingPolicy"] = ToJsonNode(properties.IndexingPolicy),
            ["uniqueKeyPolicy"] = ToJsonNode(properties.UniqueKeyPolicy),
            ["defaultTtl"] = properties.DefaultTimeToLive,
            ["conflictResolutionPolicy"] = ToJsonNode(properties.ConflictResolutionPolicy),
            ["changeFeedPolicy"] = ToJsonNode(properties.ChangeFeedPolicy),
            ["throughput"] = throughput
        };
        return new ContainerSnapshot(metadata, response.RequestCharge);
    }

    private async Task<JsonObject> ReadThroughputAsync(
        Func<Task<ThroughputResponse>> read,
        string scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var response = await read();
            var resource = response.Resource;
            return new JsonObject
            {
                ["scope"] = "dedicated",
                ["throughput"] = resource.Throughput,
                ["autoscaleMaxThroughput"] = resource.AutoscaleMaxThroughput
            };
        }
        catch (CosmosException error) when (
            error.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            logger.Debug(
                error,
                "No dedicated throughput is configured for {Scope}; it is likely inherited from the database. Diagnostics={Diagnostics}",
                scope, error.Diagnostics);
            return new JsonObject
            {
                ["scope"] = "database-shared",
                ["throughput"] = null,
                ["autoscaleMaxThroughput"] = null
            };
        }
        catch (CosmosException error)
        {
            logger.Error(error,
                "Throughput metadata read failed for {Scope}. Status={Status} ActivityId={ActivityId} Diagnostics={Diagnostics}",
                scope, error.StatusCode, error.ActivityId, error.Diagnostics);
            throw;
        }
    }

    private async Task<JsonObject> ReadArmThroughputAsync(
        Func<Task<JsonObject>> read,
        string scope)
    {
        if (armThroughput is null)
        {
            throw new InvalidOperationException(
                $"ARM throughput metadata service is required for {scope}.");
        }
        return await read();
    }

    private static JsonNode? ToJsonNode(object? value) =>
        value is null ? null : JsonNode.Parse(JsonSerializer.Serialize(value, LtrJson.Options));
}

public sealed record AccountSnapshot(
    JsonObject Metadata,
    string ConsistencyLevel,
    string[] ReadableRegions);

public sealed record DatabaseSnapshot(JsonObject Metadata, double RequestCharge);
public sealed record ContainerSnapshot(JsonObject Metadata, double RequestCharge);

public static class LtrJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
