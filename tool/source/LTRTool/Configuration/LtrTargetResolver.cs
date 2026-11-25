using System.Text.RegularExpressions;
using CosmosLab.Common;

namespace CosmosLab.LTRTool;

public sealed class LtrTargetResolver
{
    private static readonly Regex AccountNamePattern =
        new("^[a-z0-9-]{3,44}$", RegexOptions.CultureInvariant);
    private static readonly Regex StorageAccountNamePattern =
        new("^[a-z0-9]{3,24}$", RegexOptions.CultureInvariant);
    private static readonly Regex BlobContainerNamePattern =
        new("^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant);

    public TargetResolution Resolve(
        LtrOptions options,
        LtrConfiguration configuration)
    {
        var backup = configuration.Backup;
        var cloud = CloudEnvironment.For(options.CloudName);
        var subscriptionId = options.SubscriptionId ??
            Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");
        var resourceGroupName = options.ResourceGroupName;
        var managedIdentityClientId =
            options.ManagedIdentityClientId ?? backup?.ManagedIdentityClientId;
        if (!string.IsNullOrWhiteSpace(managedIdentityClientId) &&
            !Guid.TryParse(managedIdentityClientId, out _))
        {
            throw new ArgumentException("Managed identity client ID must be a GUID.");
        }
        if (options.StorageContainerName is not null &&
            !BlobContainerNamePattern.IsMatch(options.StorageContainerName))
        {
            throw new ArgumentException(
                "Storage container name must be 3-63 lowercase letters, digits, or hyphens without consecutive hyphens.");
        }

        var targets = new List<LtrTarget>();
        var defaultEndpoint = options.StorageBlobEndpoint is not null
            ? new Uri(options.StorageBlobEndpoint)
            : options.StorageAccountName is not null
                ? CreateStorageEndpoint(options.StorageAccountName, cloud.BlobSuffix)
                : new Uri(configuration.Storage.BlobEndpoint);
        var configuredReplicaEndpoint = configuration.Storage.ReplicaBlobEndpoint;
        var replicaEndpoint = options.ReplicaBlobEndpoint is not null
            ? new Uri(options.ReplicaBlobEndpoint)
            : string.IsNullOrWhiteSpace(configuredReplicaEndpoint)
                ? null
                : new Uri(configuredReplicaEndpoint);
        if (options.RequireReplica && replicaEndpoint is null)
        {
            throw new ArgumentException(
                "Replica validation requires --replica-blob-endpoint or a configured storage replica endpoint.");
        }

        if (options.Api is LtrApi.Both or LtrApi.NoSql)
        {
            targets.Add(ResolveNoSql(
                options,
                configuration,
                cloud,
                subscriptionId,
                resourceGroupName,
                managedIdentityClientId,
                defaultEndpoint,
                replicaEndpoint,
                backup?.NoSqlContainer));
        }
        if (options.Api is LtrApi.Both or LtrApi.Gremlin)
        {
            targets.Add(ResolveGremlin(
                options,
                configuration,
                cloud,
                subscriptionId,
                resourceGroupName,
                managedIdentityClientId,
                defaultEndpoint,
                replicaEndpoint,
                backup?.GremlinContainer));
        }
        return new TargetResolution(targets.ToArray(), options.Containers, options.Graphs);
    }

    private static LtrTarget ResolveNoSql(
        LtrOptions options,
        LtrConfiguration configuration,
        CloudEnvironment cloud,
        string? subscriptionId,
        string? resourceGroupName,
        string? managedIdentityClientId,
        Uri primaryBlobEndpoint,
        Uri? replicaBlobEndpoint,
        string? defaultContainer)
    {
        var configured = configuration.NoSql;
        var isExplicit = options.AccountName is not null;
        if (!isExplicit && configured is null)
        {
            throw new ArgumentException(
                "NoSQL configuration is required unless --account, --database, and --containers are provided.");
        }
        var accountName = options.AccountName ??
            AccountFromEndpoint(new Uri(configured!.Endpoint));
        ValidateAccountName(accountName);
        var endpoint = !isExplicit
            ? new Uri(configured!.Endpoint)
            : new Uri($"https://{accountName}.{cloud.DocumentSuffix}/");
        var database = options.DatabaseName ??
            configured?.Database ??
            throw new ArgumentException(
                "An explicit NoSQL account requires --database.");
        LabConfiguration.ValidateIdentifier(database, "NoSQL database");
        var resources = SelectResources(
            options.Containers,
            configured?.Containers ?? [],
            "NoSQL containers",
            isExplicit);
        var accountResourceId = !isExplicit
            ? ResolveConfiguredNoSqlResourceId(
                configured!,
                configuration.Gremlin?.AccountResourceId,
                accountName,
                subscriptionId,
                resourceGroupName)
            : CreateAccountResourceId(
                accountName, subscriptionId, resourceGroupName, "explicit NoSQL account");
        var containerName = ResolveStorageContainer(
            options.StorageContainerName,
            defaultContainer,
            configuration.Storage.Containers,
            "NoSQL");
        return new LtrTarget(
            LtrApi.NoSql,
            accountName,
            accountResourceId,
            endpoint,
            database,
            resources,
            primaryBlobEndpoint,
            replicaBlobEndpoint,
            containerName,
            managedIdentityClientId);
    }

    private static LtrTarget ResolveGremlin(
        LtrOptions options,
        LtrConfiguration configuration,
        CloudEnvironment cloud,
        string? subscriptionId,
        string? resourceGroupName,
        string? managedIdentityClientId,
        Uri primaryBlobEndpoint,
        Uri? replicaBlobEndpoint,
        string? defaultContainer)
    {
        var configured = configuration.Gremlin;
        var isExplicit = options.AccountName is not null;
        if (!isExplicit && configured is null)
        {
            throw new ArgumentException(
                "Gremlin configuration is required unless --account, --database, and --graphs are provided.");
        }
        var configuredResourceId = configured?.AccountResourceId.Trim('/');
        var configuredAccountName = configuredResourceId?.Split('/')[^1];
        var accountName = options.AccountName ?? configuredAccountName;
        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new ArgumentException("Gremlin account name was not configured.");
        }
        ValidateAccountName(accountName);
        var accountResourceId = !isExplicit
            ? configured!.AccountResourceId
            : CreateAccountResourceId(
                accountName, subscriptionId, resourceGroupName, "explicit Gremlin account");
        var dataEndpoint = !isExplicit
            ? new Uri($"https://{configured!.Hostname}")
            : new Uri($"https://{accountName}.{cloud.GremlinSuffix}");
        var database = options.DatabaseName ??
            configured?.Database ??
            throw new ArgumentException(
                "An explicit Gremlin account requires --database.");
        LabConfiguration.ValidateIdentifier(database, "Gremlin database");
        var resources = SelectResources(
            options.Graphs,
            configured?.Graphs ?? [],
            "Gremlin graphs",
            isExplicit);
        var containerName = ResolveStorageContainer(
            options.StorageContainerName,
            defaultContainer,
            configuration.Storage.Containers,
            "Gremlin");
        return new LtrTarget(
            LtrApi.Gremlin,
            accountName,
            accountResourceId,
            dataEndpoint,
            database,
            resources,
            primaryBlobEndpoint,
            replicaBlobEndpoint,
            containerName,
            managedIdentityClientId);
    }

    private static string CreateAccountResourceId(
        string accountName,
        string? subscriptionId,
        string? resourceGroupName,
        string description)
    {
        if (!Guid.TryParse(subscriptionId, out var subscription))
        {
            throw new ArgumentException(
                $"{description} requires --subscription-id or AZURE_SUBSCRIPTION_ID.");
        }
        if (string.IsNullOrWhiteSpace(resourceGroupName))
        {
            throw new ArgumentException(
                $"{description} requires --resource-group or COSMOS_LTR_RESOURCE_GROUP.");
        }
        return $"/subscriptions/{subscription}/resourceGroups/{resourceGroupName}/" +
               $"providers/Microsoft.DocumentDB/databaseAccounts/{accountName}";
    }

    private static string[] SelectResources(
        string[] requested,
        string[] configured,
        string description,
        bool explicitAccount)
    {
        if (requested.Length == 0)
        {
            if (explicitAccount)
            {
                throw new ArgumentException(
                    $"An explicit account requires {description} to be supplied.");
            }
            return configured;
        }
        var duplicates = requested
            .GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length != 0)
        {
            throw new ArgumentException(
                $"Duplicate {description}: {string.Join(", ", duplicates)}.");
        }
        if (!explicitAccount)
        {
            var unknown = requested.Except(configured, StringComparer.Ordinal).ToArray();
            if (unknown.Length != 0)
            {
                throw new ArgumentException(
                    $"Unknown {description}: {string.Join(", ", unknown)}. " +
                    $"Configured values: {string.Join(", ", configured)}.");
            }
        }
        foreach (var resource in requested)
        {
            LabConfiguration.ValidateIdentifier(resource, description);
        }
        return requested;
    }

    private static string AccountFromEndpoint(Uri endpoint) =>
        endpoint.Host.Split('.')[0];

    private static string CreateSameScopeAccountResourceId(
        string accountName,
        string configuredAccountResourceId)
    {
        var separator = configuredAccountResourceId.LastIndexOf('/');
        if (separator < 0)
        {
            throw new InvalidDataException(
                "The configured Gremlin accountResourceId cannot provide the NoSQL account scope.");
        }
        return $"{configuredAccountResourceId[..separator]}/{accountName}";
    }

    private static string ResolveConfiguredNoSqlResourceId(
        LtrNoSqlSettings configured,
        string? gremlinResourceId,
        string accountName,
        string? subscriptionId,
        string? resourceGroupName)
    {
        if (!string.IsNullOrWhiteSpace(configured.AccountResourceId))
        {
            return configured.AccountResourceId;
        }
        if (!string.IsNullOrWhiteSpace(gremlinResourceId))
        {
            return CreateSameScopeAccountResourceId(accountName, gremlinResourceId);
        }
        return CreateAccountResourceId(
            accountName,
            subscriptionId,
            resourceGroupName,
            "configured NoSQL account");
    }

    private static string ResolveStorageContainer(
        string? explicitContainer,
        string? preferredContainer,
        string[]? configuredContainers,
        string apiName)
    {
        var result = explicitContainer ?? preferredContainer;
        if (string.IsNullOrWhiteSpace(result) &&
            configuredContainers?.Length == 1)
        {
            result = configuredContainers[0];
        }
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new ArgumentException(
                $"{apiName} backup requires --storage-container or a configured " +
                $"{apiName} storage container.");
        }
        if (!BlobContainerNamePattern.IsMatch(result))
        {
            throw new ArgumentException(
                "Storage container name must be 3-63 lowercase letters, digits, or hyphens.");
        }
        if (configuredContainers is { Length: > 0 } &&
            !configuredContainers.Contains(result, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Storage container '{result}' is not in the configured storage container list.");
        }
        return result;
    }

    private static Uri CreateStorageEndpoint(string accountName, string blobSuffix)
    {
        if (!StorageAccountNamePattern.IsMatch(accountName))
        {
            throw new ArgumentException("Storage account name is invalid.");
        }
        return new Uri($"https://{accountName}.{blobSuffix}/");
    }

    private static void ValidateAccountName(string accountName)
    {
        if (!AccountNamePattern.IsMatch(accountName))
        {
            throw new ArgumentException(
                "Cosmos account name must be lowercase letters, digits, or hyphens.");
        }
    }
}

public sealed record TargetResolution(
    LtrTarget[] Targets,
    string[] RequestedContainers,
    string[] RequestedGraphs);
