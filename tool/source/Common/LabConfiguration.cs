using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;

namespace CosmosLab.Common;

public sealed record NoSqlSettings(string Endpoint, string Database, string[] Containers);
public sealed record GremlinSettings(string AccountResourceId, string Hostname, string Database, string[] Graphs);
public sealed record StorageSettings(
    string BlobEndpoint,
    string[] Containers,
    string? ReplicaBlobEndpoint = null);
public sealed record BackupSettings(
    string ManagedIdentityClientId,
    int RetentionMonths,
    string NoSqlContainer,
    string GremlinContainer);

public sealed record LabConfiguration(
    string CloudName,
    NoSqlSettings NoSql,
    GremlinSettings Gremlin,
    StorageSettings Storage,
    string PrivateEndpointSubnet,
    BackupSettings? Backup = null)
{
    public static LabConfiguration Load(string path)
    {
        var configuration = JsonSerializer.Deserialize<LabConfiguration>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidDataException("The lab configuration is empty.");
        configuration.Validate();
        return configuration;
    }

    public void Validate()
    {
        var cloud = CloudEnvironment.For(CloudName);
        ArgumentNullException.ThrowIfNull(NoSql);
        ArgumentNullException.ThrowIfNull(Gremlin);
        ArgumentNullException.ThrowIfNull(Storage);
        ValidateEndpoint(NoSql.Endpoint, cloud.DocumentSuffix);
        ValidateEndpoint(Storage.BlobEndpoint, cloud.BlobSuffix);
        if (!string.IsNullOrWhiteSpace(Storage.ReplicaBlobEndpoint))
        {
            ValidateEndpoint(Storage.ReplicaBlobEndpoint, cloud.BlobSuffix);
        }
        ValidateIdentifier(NoSql.Database, "NoSQL database");
        ValidateIdentifier(Gremlin.Database, "Gremlin database");
        ValidateNames(NoSql.Containers, 2, 25, "NoSQL containers");
        ValidateNames(Gremlin.Graphs, 2, 25, "Gremlin graphs");
        ValidateNames(Storage.Containers, 2, 2, "backup containers");
        if (Backup is not null)
        {
            if (!Guid.TryParse(Backup.ManagedIdentityClientId, out _))
            {
                throw new ArgumentException("Backup managedIdentityClientId must be a client GUID.");
            }
            if (Backup.RetentionMonths is < 1 or > 120)
            {
                throw new ArgumentException("Backup retentionMonths must be from 1 to 120.");
            }
            if (!Storage.Containers.Contains(Backup.NoSqlContainer, StringComparer.Ordinal) ||
                !Storage.Containers.Contains(Backup.GremlinContainer, StringComparer.Ordinal) ||
                Backup.NoSqlContainer == Backup.GremlinContainer)
            {
                throw new ArgumentException("Backup NoSQL and Gremlin container names must identify distinct configured storage containers.");
            }
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(PrivateEndpointSubnet);
        ArgumentException.ThrowIfNullOrWhiteSpace(Gremlin.AccountResourceId);

        var segments = Gremlin.AccountResourceId.Split('/');
        if (segments.Length != 9 || segments[0] != "" ||
            segments[1] != "subscriptions" || !Guid.TryParse(segments[2], out _) ||
            segments[3] != "resourceGroups" || string.IsNullOrWhiteSpace(segments[4]) ||
            segments[5] != "providers" || segments[6] != "Microsoft.DocumentDB" ||
            segments[7] != "databaseAccounts" || string.IsNullOrWhiteSpace(segments[8]) ||
            !string.Equals(Gremlin.Hostname, $"{segments[8]}.{cloud.GremlinSuffix}", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Gremlin accountResourceId and hostname must identify the same account in the selected cloud.");
        }
    }

    public static void ValidateIdentifier(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.IndexOfAny(['/', '\\', '?', '#']) >= 0)
        {
            throw new ArgumentException($"{description} must be a nonempty Cosmos ID of at most 255 characters without /, \\, ?, or #.");
        }
    }

    private static void ValidateNames(string[] names, int minimum, int maximum, string description)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Length < minimum || names.Length > maximum ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new ArgumentException($"{description} must contain {minimum} to {maximum} distinct names.");
        }
        foreach (var name in names)
        {
            ValidateIdentifier(name, description);
        }
    }

    private static void ValidateEndpoint(string endpoint, string hostSuffix)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo != "" ||
            uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" ||
            !uri.Host.EndsWith($".{hostSuffix}", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The service endpoint must be an HTTPS account endpoint in {hostSuffix}.");
        }
    }
}

public sealed record CloudEnvironment(
    Uri AuthorityHost,
    Uri ManagementEndpoint,
    string DocumentSuffix,
    string GremlinSuffix,
    string BlobSuffix)
{
    public static CloudEnvironment For(string name) => name switch
    {
        "AzureChinaCloud" => new(
            AzureAuthorityHosts.AzureChina,
            new Uri("https://management.chinacloudapi.cn/"),
            "documents.azure.cn",
            "gremlin.cosmos.azure.cn",
            "blob.core.chinacloudapi.cn"),
        "AzureCloud" => new(
            AzureAuthorityHosts.AzurePublicCloud,
            new Uri("https://management.azure.com/"),
            "documents.azure.com",
            "gremlin.cosmos.azure.com",
            "blob.core.windows.net"),
        _ => throw new ArgumentException("cloudName must be AzureChinaCloud or AzureCloud.")
    };

    public DefaultAzureCredential CreateCredential(string? managedIdentityClientId = null) => new(new DefaultAzureCredentialOptions
    {
        AuthorityHost = AuthorityHost,
        ManagedIdentityClientId = managedIdentityClientId,
        ExcludeInteractiveBrowserCredential = true
    });

    public async Task<string> ReadGremlinKeyAsync(TokenCredential credential, string accountResourceId)
    {
        var scope = $"{ManagementEndpoint.AbsoluteUri.TrimEnd('/')}/.default";
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), CancellationToken.None);
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(ManagementEndpoint, $"{accountResourceId}/listKeys?api-version=2024-05-15"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!document.RootElement.TryGetProperty("primaryMasterKey", out var property) ||
            property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException("ARM did not return a usable Gremlin account key.");
        }
        return property.GetString()!;
    }
}
