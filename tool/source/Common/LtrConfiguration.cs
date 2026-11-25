using System.Text.Json;
using System.Text.Json.Serialization;

namespace CosmosLab.Common;

public sealed record LtrNoSqlSettings(
    string Endpoint,
    string Database,
    string[] Containers,
    string? AccountResourceId = null);

public sealed record LtrGremlinSettings(
    string AccountResourceId,
    string Hostname,
    string Database,
    string[] Graphs);

public sealed record LtrStorageSettings(
    string BlobEndpoint,
    string[]? Containers = null,
    string? ReplicaBlobEndpoint = null);

public sealed record LtrBackupSettings(
    string? ManagedIdentityClientId = null,
    int RetentionMonths = 13,
    string? NoSqlContainer = null,
    string? GremlinContainer = null);

public sealed record LtrConfiguration(
    string CloudName,
    LtrStorageSettings Storage,
    LtrNoSqlSettings? NoSql = null,
    LtrGremlinSettings? Gremlin = null,
    LtrBackupSettings? Backup = null,
    string? PrivateEndpointSubnet = null)
{
    public static LtrConfiguration Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "LTR configuration file was not found.",
                path);
        }
        var configuration = JsonSerializer.Deserialize<LtrConfiguration>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidDataException("The LTR configuration is empty.");
        configuration.Validate();
        return configuration;
    }

    public void Validate()
    {
        var cloud = CloudEnvironment.For(CloudName);
        ArgumentNullException.ThrowIfNull(Storage);
        ValidateEndpoint(Storage.BlobEndpoint, cloud.BlobSuffix, "Storage Blob endpoint");
        if (!string.IsNullOrWhiteSpace(Storage.ReplicaBlobEndpoint))
        {
            ValidateEndpoint(
                Storage.ReplicaBlobEndpoint,
                cloud.BlobSuffix,
                "Storage replica Blob endpoint");
        }
        if (Storage.Containers is not null)
        {
            ValidateNames(Storage.Containers, 1, 25, "backup storage containers");
        }

        if (NoSql is not null)
        {
            ValidateEndpoint(NoSql.Endpoint, cloud.DocumentSuffix, "NoSQL endpoint");
            LabConfiguration.ValidateIdentifier(NoSql.Database, "NoSQL database");
            ValidateNames(NoSql.Containers, 1, 25, "NoSQL containers");
            if (!string.IsNullOrWhiteSpace(NoSql.AccountResourceId))
            {
                ValidateAccountResourceId(NoSql.AccountResourceId);
            }
        }

        if (Gremlin is not null)
        {
            LabConfiguration.ValidateIdentifier(Gremlin.Database, "Gremlin database");
            ValidateNames(Gremlin.Graphs, 1, 25, "Gremlin graphs");
            ValidateAccountResourceId(Gremlin.AccountResourceId);
            var accountName = Gremlin.AccountResourceId.Trim('/').Split('/')[^1];
            if (!string.Equals(
                Gremlin.Hostname.TrimEnd('.'),
                $"{accountName}.{cloud.GremlinSuffix}",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The Gremlin accountResourceId and hostname must identify the same account.");
            }
        }

        if (Backup is not null)
        {
            if (!string.IsNullOrWhiteSpace(Backup.ManagedIdentityClientId) &&
                !Guid.TryParse(Backup.ManagedIdentityClientId, out _))
            {
                throw new ArgumentException(
                    "Backup managedIdentityClientId must be a GUID when provided.");
            }
            if (Backup.RetentionMonths is < 1 or > 120)
            {
                throw new ArgumentException(
                    "Backup retentionMonths must be from 1 to 120.");
            }
            if (!string.IsNullOrWhiteSpace(Backup.NoSqlContainer))
            {
                LabConfiguration.ValidateIdentifier(
                    Backup.NoSqlContainer,
                    "NoSQL backup storage container");
            }
            if (!string.IsNullOrWhiteSpace(Backup.GremlinContainer))
            {
                LabConfiguration.ValidateIdentifier(
                    Backup.GremlinContainer,
                    "Gremlin backup storage container");
            }
        }
    }

    private static void ValidateNames(
        string[] names,
        int minimum,
        int maximum,
        string description)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Length < minimum ||
            names.Length > maximum ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new ArgumentException(
                $"{description} must contain {minimum} to {maximum} distinct names.");
        }
        foreach (var name in names)
        {
            LabConfiguration.ValidateIdentifier(name, description);
        }
    }

    private static void ValidateEndpoint(
        string endpoint,
        string hostSuffix,
        string description)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 ||
            uri.UserInfo != "" ||
            uri.AbsolutePath != "/" ||
            uri.Query != "" ||
            uri.Fragment != "" ||
            !uri.Host.EndsWith($".{hostSuffix}", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{description} must be an HTTPS account endpoint in {hostSuffix}.");
        }
    }

    private static void ValidateAccountResourceId(string resourceId)
    {
        var segments = resourceId.Trim('/').Split('/');
        if (segments.Length != 8 ||
            segments[0] != "subscriptions" ||
            !Guid.TryParse(segments[1], out _) ||
            segments[2] != "resourceGroups" ||
            string.IsNullOrWhiteSpace(segments[3]) ||
            segments[4] != "providers" ||
            segments[5] != "Microsoft.DocumentDB" ||
            segments[6] != "databaseAccounts" ||
            string.IsNullOrWhiteSpace(segments[7]))
        {
            throw new ArgumentException(
                "Cosmos accountResourceId must be a full Azure resource ID.");
        }
    }
}
