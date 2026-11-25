using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using CosmosLab.Common;
using Microsoft.Azure.Cosmos;
using Serilog;

namespace CosmosLab.LTRTool;

public sealed class ArmThroughputMetadataService
{
    private readonly CloudEnvironment cloud;
    private readonly TokenCredential credential;
    private readonly ILogger logger;
    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public ArmThroughputMetadataService(
        CloudEnvironment cloud,
        TokenCredential credential,
        ILogger logger)
    {
        this.cloud = cloud;
        this.credential = credential;
        this.logger = logger.ForContext<ArmThroughputMetadataService>();
    }

    public Task<JsonObject> ReadDatabaseAsync(
        LtrTarget target,
        CancellationToken cancellationToken) =>
        ReadAsync(
            $"{target.AccountResourceId}/sqlDatabases/" +
            $"{Uri.EscapeDataString(target.DatabaseName)}/throughputSettings/default",
            $"database/{target.DatabaseName}",
            "database-shared",
            cancellationToken);

    public Task<JsonObject> ReadContainerAsync(
        LtrTarget target,
        string containerName,
        CancellationToken cancellationToken) =>
        ReadAsync(
            $"{target.AccountResourceId}/sqlDatabases/" +
            $"{Uri.EscapeDataString(target.DatabaseName)}/containers/" +
            $"{Uri.EscapeDataString(containerName)}/throughputSettings/default",
            $"container/{target.DatabaseName}/{containerName}",
            "database-shared",
            cancellationToken);

    public async Task<AccountSnapshot> ReadGremlinAccountAsync(
        LtrTarget target,
        CancellationToken cancellationToken)
    {
        var root = await ReadResourceAsync(
            target.AccountResourceId,
            $"account/{target.AccountName}",
            allowNotFound: false,
            cancellationToken);
        return ParseGremlinAccount(root!, target);
    }

    public static AccountSnapshot ParseGremlinAccount(
        JsonObject root,
        LtrTarget target)
    {
        var properties = root!["properties"]?.AsObject()
            ?? throw new InvalidDataException("ARM Gremlin account response omitted properties.");
        var consistency = properties["consistencyPolicy"]?.AsObject();
        var consistencyLevel = consistency?["defaultConsistencyLevel"]?.GetValue<string>()
            ?? "unknown";
        var writeRegions = ReadRegionNames(properties, "writeLocations");
        var readRegions = ReadRegionNames(properties, "readLocations");
        var metadata = new JsonObject
        {
            ["id"] = root["name"]?.DeepClone(),
            ["resourceId"] = target.AccountResourceId,
            ["endpoint"] = target.DataEndpoint.AbsoluteUri.TrimEnd('/'),
            ["consistency"] = consistencyLevel,
            ["writableRegions"] = new JsonArray(
                writeRegions.Select(name => JsonValue.Create(name)).ToArray()),
            ["readableRegions"] = new JsonArray(
                readRegions.Select(name => JsonValue.Create(name)).ToArray()),
            ["sdkName"] = "Microsoft.Azure.Cosmos.Gremlin",
            ["sdkVersion"] = typeof(Gremlin.Net.Driver.GremlinClient).Assembly
                .GetName().Version?.ToString() ?? "unknown"
        };
        if (consistency?["maxIntervalInSeconds"] is { } maxInterval)
        {
            metadata["maxIntervalInSeconds"] = maxInterval.DeepClone();
        }
        if (consistency?["maxStalenessPrefix"] is { } maxStalenessPrefix)
        {
            metadata["maxStalenessPrefix"] = maxStalenessPrefix.DeepClone();
        }
        return new AccountSnapshot(metadata, consistencyLevel, readRegions);
    }

    public async Task<DatabaseSnapshot> ReadGremlinDatabaseAsync(
        LtrTarget target,
        CancellationToken cancellationToken)
    {
        var databasePath = $"{target.AccountResourceId}/gremlinDatabases/" +
            Uri.EscapeDataString(target.DatabaseName);
        var root = await ReadResourceAsync(
            databasePath,
            $"gremlin-database/{target.DatabaseName}",
            allowNotFound: false,
            cancellationToken);
        var throughput = await ReadAsync(
            $"{databasePath}/throughputSettings/default",
            $"gremlin-database/{target.DatabaseName}",
            "account-shared",
            cancellationToken);
        return ParseGremlinDatabase(root!, throughput, target.DatabaseName);
    }

    public async Task<ContainerSnapshot> ReadGremlinGraphAsync(
        LtrTarget target,
        string graphName,
        CancellationToken cancellationToken)
    {
        var graphPath = $"{target.AccountResourceId}/gremlinDatabases/" +
            $"{Uri.EscapeDataString(target.DatabaseName)}/graphs/" +
            Uri.EscapeDataString(graphName);
        var root = await ReadResourceAsync(
            graphPath,
            $"gremlin-graph/{target.DatabaseName}/{graphName}",
            allowNotFound: false,
            cancellationToken);
        var throughput = await ReadAsync(
            $"{graphPath}/throughputSettings/default",
            $"gremlin-graph/{target.DatabaseName}/{graphName}",
            "database-shared",
            cancellationToken);
        return ParseGremlinGraph(root!, throughput, graphName);
    }

    public static DatabaseSnapshot ParseGremlinDatabase(
        JsonObject root,
        JsonObject throughput,
        string databaseName)
    {
        var resource = ReadArmResource(root);
        return new DatabaseSnapshot(
            new JsonObject
            {
                ["id"] = resource["id"]?.DeepClone() ?? JsonValue.Create(databaseName),
                ["throughput"] = throughput
            },
            0);
    }

    public static ContainerSnapshot ParseGremlinGraph(
        JsonObject root,
        JsonObject throughput,
        string graphName)
    {
        var resource = ReadArmResource(root);
        var metadata = new JsonObject
        {
            ["id"] = resource["id"]?.DeepClone() ?? JsonValue.Create(graphName),
            ["partitionKey"] = resource["partitionKey"]?.DeepClone(),
            ["indexingPolicy"] = resource["indexingPolicy"]?.DeepClone(),
            ["uniqueKeyPolicy"] = resource["uniqueKeyPolicy"]?.DeepClone(),
            ["defaultTtl"] = resource["defaultTtl"]?.DeepClone(),
            ["conflictResolutionPolicy"] =
                resource["conflictResolutionPolicy"]?.DeepClone(),
            ["changeFeedPolicy"] = resource["changeFeedPolicy"]?.DeepClone(),
            ["throughput"] = throughput
        };
        return new ContainerSnapshot(metadata, 0);
    }

    private async Task<JsonObject> ReadAsync(
        string resourcePath,
        string scopeName,
        string missingScope,
        CancellationToken cancellationToken)
    {
        var root = await ReadResourceAsync(
            resourcePath,
            scopeName,
            allowNotFound: true,
            cancellationToken);
        if (root is null)
        {
            return new JsonObject
            {
                ["scope"] = missingScope,
                ["throughput"] = null,
                ["autoscaleMaxThroughput"] = null
            };
        }
        var resource = ReadArmResource(root);
        using var resourceDocument = JsonDocument.Parse(resource.ToJsonString());
        return ParseThroughputMetadata(resourceDocument.RootElement, "dedicated");
    }

    private async Task<JsonObject?> ReadResourceAsync(
        string resourcePath,
        string scopeName,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 8;
        var scope = $"{cloud.ManagementEndpoint.AbsoluteUri.TrimEnd('/')}/.default";
        for (var attempt = 1; ; attempt++)
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext([scope]),
                cancellationToken);
            var uri = new Uri(
                cloud.ManagementEndpoint,
                $"{resourcePath.TrimStart('/')}?api-version=2024-05-15");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", token.Token);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
            {
                return null;
            }
            if (response.IsSuccessStatusCode)
            {
                await using var content = await response.Content.ReadAsStreamAsync(
                    cancellationToken);
                using var document = await JsonDocument.ParseAsync(
                    content,
                    cancellationToken: cancellationToken);
                return JsonNode.Parse(document.RootElement.GetRawText())?.AsObject()
                    ?? throw new InvalidDataException(
                        $"ARM response for {scopeName} was empty.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (attempt < maxAttempts &&
                (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
                 response.StatusCode == HttpStatusCode.TooManyRequests ||
                 (int)response.StatusCode >= 500))
            {
                var delay = GetRetryDelay(response, attempt);
                logger.Warning(
                    "ARM throughput read for {Scope} returned {Status}; retry {Attempt}/{Maximum} in {DelayMs:F0} ms. Body={Body}",
                    scopeName,
                    (int)response.StatusCode,
                    attempt,
                    maxAttempts,
                    delay.TotalMilliseconds,
                    body);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            logger.Error(
                "ARM throughput read for {Scope} failed. Status={Status} Body={Body}",
                scopeName, (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
            throw new InvalidOperationException("Unreachable ARM throughput response branch.");
        }
    }

    private static JsonObject ReadArmResource(JsonObject root) =>
        root["properties"]?["resource"]?.AsObject()
        ?? throw new InvalidDataException(
            "ARM Cosmos response did not contain properties.resource.");

    private static string[] ReadRegionNames(JsonObject properties, string propertyName)
    {
        var regions = properties[propertyName]?.AsArray()
            ?? throw new InvalidDataException(
                $"ARM account metadata did not contain {propertyName}.");
        return regions
            .Select(region => region?["locationName"]?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
    }

    public static JsonObject ParseThroughputMetadata(JsonElement resource, string scope)
    {
        int? throughput = null;
        int? autoscaleMaximum = null;
        if (resource.TryGetProperty("throughput", out var throughputProperty) &&
            throughputProperty.ValueKind != JsonValueKind.Null &&
            throughputProperty.TryGetInt32(out var throughputValue))
        {
            throughput = throughputValue;
        }
        if (resource.TryGetProperty("autoscaleSettings", out var autoscale) &&
            autoscale.ValueKind == JsonValueKind.Object &&
            autoscale.TryGetProperty("maxThroughput", out var maxThroughput) &&
            maxThroughput.TryGetInt32(out var maxThroughputValue))
        {
            autoscaleMaximum = maxThroughputValue;
        }
        return new JsonObject
        {
            ["scope"] = scope,
            ["throughput"] = throughput,
            ["autoscaleMaxThroughput"] = autoscaleMaximum,
            ["minimumThroughput"] = ReadString(resource, "minimumThroughput"),
            ["instantMaximumThroughput"] = ReadString(resource, "instantMaximumThroughput"),
            ["softAllowedMaximumThroughput"] = ReadString(
                resource, "softAllowedMaximumThroughput")
        };
    }

    private static string? ReadString(JsonElement resource, string propertyName)
    {
        if (!resource.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }
        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
        }
        return TimeSpan.FromMilliseconds(Math.Min(
            250 * Math.Pow(2, attempt - 1), 30_000));
    }
}
