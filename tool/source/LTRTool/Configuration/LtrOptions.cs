using System.Globalization;
using System.Text.RegularExpressions;

namespace CosmosLab.LTRTool;

public enum LtrApi
{
    Both,
    NoSql,
    Gremlin
}

public enum LtrCommand
{
    Export,
    Inventory
}

public sealed record LtrOptions(
    LtrCommand Command,
    LtrApi Api,
    string ConfigurationPath,
    string CloudName,
    string? SubscriptionId,
    string? ResourceGroupName,
    string? AccountName,
    string? SourceRegion,
    string? DatabaseName,
    string[] Containers,
    string[] Graphs,
    string? StorageAccountName,
    string? StorageContainerName,
    string? StorageBlobEndpoint,
    string? ReplicaBlobEndpoint,
    string? ManagedIdentityClientId,
    string? BackupId,
    string? BackupMonth,
    string WorkingDirectory,
    int Parallelism,
    double MaxRuPerSecond,
    int RetentionMonths,
    int ChangeFeedCatchUpSeconds,
    int SampleQueryCount,
    bool RequireReplica,
    bool Help,
    string LogLevel)
{
    public static LtrOptions Parse(string[] arguments)
    {
        var values = ParseArguments(arguments);
        if (values.Remove("--help", out var help) || arguments is ["help"])
        {
            throw new LtrHelpRequestedException();
        }
        var command = arguments.Length > 0 ? arguments[0] : "export";
        if (command.StartsWith("--", StringComparison.Ordinal))
        {
            command = "export";
        }
        var parsedCommand = command switch
        {
            "export" or "backup" => LtrCommand.Export,
            "inventory" or "list" => LtrCommand.Inventory,
            _ => throw new ArgumentException("Supported commands: export, inventory.")
        };

        var cloudName = Resolve(values, "--cloud", "COSMOS_LTR_CLOUD", "AzureChinaCloud");
        var options = new LtrOptions(
            parsedCommand,
            ParseApi(Resolve(values, "--api", "COSMOS_LTR_API", "Both")),
            ResolveRequired(values, "--config", "COSMOS_LTR_CONFIG", "/opt/cosmos-ltr/config.json"),
            cloudName,
            ResolveOptional(values, "--subscription-id", "COSMOS_LTR_SUBSCRIPTION_ID", "AZURE_SUBSCRIPTION_ID"),
            ResolveOptional(values, "--resource-group", "COSMOS_LTR_RESOURCE_GROUP"),
            ResolveOptional(values, "--account", "COSMOS_LTR_ACCOUNT_NAME"),
            ResolveOptional(values, "--source-region", "COSMOS_LTR_SOURCE_REGION"),
            ResolveOptional(values, "--database", "COSMOS_LTR_DATABASE"),
            ParseList(ResolveOptional(values, "--containers", "COSMOS_LTR_CONTAINERS")),
            ParseList(ResolveOptional(values, "--graphs", "COSMOS_LTR_GRAPHS")),
            ResolveOptional(values, "--storage-account", "COSMOS_LTR_STORAGE_ACCOUNT"),
            ResolveOptional(values, "--storage-container", "COSMOS_LTR_STORAGE_CONTAINER"),
            ResolveOptional(values, "--storage-blob-endpoint", "COSMOS_LTR_STORAGE_BLOB_ENDPOINT"),
            ResolveOptional(values, "--replica-blob-endpoint", "COSMOS_LTR_REPLICA_BLOB_ENDPOINT"),
            ResolveOptional(values, "--managed-identity-client-id", "COSMOS_LTR_MANAGED_IDENTITY_CLIENT_ID"),
            ResolveOptional(values, "--backup-id", "COSMOS_LTR_BACKUP_ID"),
            ResolveOptional(values, "--month", "COSMOS_LTR_MONTH"),
            ResolveRequired(values, "--working-directory", "COSMOS_LTR_WORKING_DIRECTORY",
                Path.Combine(Path.GetTempPath(), "cosmos-ltr")),
            ParseInt(values, "--parallelism", "COSMOS_LTR_PARALLELISM", 4, 1, 64),
            ParseDouble(values, "--max-ru-per-second", "COSMOS_LTR_MAX_RU_PER_SECOND", 200, 1, 1_000_000),
            ParseInt(values, "--retention-months", "COSMOS_LTR_RETENTION_MONTHS", 13, 1, 120),
            ParseInt(values, "--change-feed-catch-up-seconds", "COSMOS_LTR_CHANGE_FEED_CATCH_UP_SECONDS",
                300, 0, 3600),
            ParseInt(values, "--sample-query-count", "COSMOS_LTR_SAMPLE_QUERY_COUNT", 3, 1, 100),
            ParseBool(values, "--replica-required", "COSMOS_LTR_REPLICA_REQUIRED", true),
            false,
            Resolve(values, "--log-level", "COSMOS_LTR_LOG_LEVEL", "Information"));

        if (values.Count != 0)
        {
            throw new ArgumentException($"Unknown option '{values.Keys.First()}'.");
        }
        if (options.AccountName is not null && options.Api == LtrApi.Both)
        {
            throw new ArgumentException("Specify --api NoSql or --api Gremlin when --account is provided.");
        }
        if (options.BackupMonth is not null &&
            !DateTime.TryParseExact(options.BackupMonth, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
        {
            throw new ArgumentException("--month must use yyyy-MM.");
        }
        if (options.ManagedIdentityClientId is not null &&
            !Guid.TryParse(options.ManagedIdentityClientId, out _))
        {
            throw new ArgumentException("--managed-identity-client-id must be a GUID.");
        }
        if (options.StorageBlobEndpoint is not null &&
            !IsSafeHttpsEndpoint(options.StorageBlobEndpoint))
        {
            throw new ArgumentException("--storage-blob-endpoint must be an absolute URI.");
        }
        if (options.ReplicaBlobEndpoint is not null &&
            !IsSafeHttpsEndpoint(options.ReplicaBlobEndpoint))
        {
            throw new ArgumentException("--replica-blob-endpoint must be an absolute URI.");
        }
        if (options.Api == LtrApi.Gremlin && options.Containers.Length != 0)
        {
            throw new ArgumentException("--containers is only valid for NoSql targets.");
        }
        if (options.Api == LtrApi.NoSql && options.Graphs.Length != 0)
        {
            throw new ArgumentException("--graphs is only valid for Gremlin targets.");
        }
        if (options.BackupId is not null &&
            !Regex.IsMatch(
                options.BackupId,
                "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
                RegexOptions.CultureInvariant))
        {
            throw new ArgumentException(
                "--backup-id may contain only letters, digits, '.', '_', or '-' and cannot contain path separators.");
        }
        return options;
    }

    public static string Usage => """
LTRTool export|inventory
  --config FILE
  [--cloud AzureCloud|AzureChinaCloud]
  [--api Both|NoSql|Gremlin]
  [--subscription-id GUID] [--resource-group NAME]
  [--account NAME] [--source-region REGION] [--database NAME]
  [--containers customers,orders] [--graphs social,catalog]
  [--storage-account NAME] [--storage-container NAME]
  [--storage-blob-endpoint URI] [--replica-blob-endpoint URI]
  [--managed-identity-client-id GUID]
  [--backup-id ID] [--month yyyy-MM]
  [--parallelism 1..64] [--max-ru-per-second N]
  [--retention-months 1..120]
  [--change-feed-catch-up-seconds 0..3600]
  [--sample-query-count 1..100]
  [--replica-required true|false]
  [--working-directory DIR] [--log-level Information]

Environment equivalents use COSMOS_LTR_* names, for example:
COSMOS_LTR_ACCOUNT_NAME, COSMOS_LTR_SUBSCRIPTION_ID,
COSMOS_LTR_RESOURCE_GROUP, COSMOS_LTR_STORAGE_BLOB_ENDPOINT.
""";

    private static Dictionary<string, string> ParseArguments(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = arguments.Length > 0 && !arguments[0].StartsWith("--", StringComparison.Ordinal) ? 1 : 0;
        for (var index = start; index < arguments.Length; index++)
        {
            var option = arguments[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Expected an option, found '{option}'.");
            }
            if (option is "--help")
            {
                values[option] = "";
                continue;
            }
            if (index + 1 >= arguments.Length ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(arguments[index + 1]))
            {
                throw new ArgumentException($"Option '{option}' requires a value.");
            }
            if (!values.TryAdd(option, arguments[++index]))
            {
                throw new ArgumentException($"Duplicate option '{option}'.");
            }
        }
        return values;
    }

    private static string Resolve(
        Dictionary<string, string> values,
        string option,
        string environmentVariable,
        string defaultValue)
    {
        if (values.Remove(option, out var value))
        {
            return value;
        }
        return Environment.GetEnvironmentVariable(environmentVariable) ?? defaultValue;
    }

    private static string ResolveRequired(
        Dictionary<string, string> values,
        string option,
        string environmentVariable,
        string defaultValue)
    {
        var result = Resolve(values, option, environmentVariable, defaultValue);
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new ArgumentException($"{option} cannot be empty.");
        }
        return result;
    }

    private static string? ResolveOptional(
        Dictionary<string, string> values,
        string option,
        params string[] environmentVariables)
    {
        if (values.Remove(option, out var value))
        {
            return value;
        }
        foreach (var variable in environmentVariables)
        {
            var environmentValue = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                return environmentValue;
            }
        }
        return null;
    }

    private static int ParseInt(
        Dictionary<string, string> values,
        string option,
        string environmentVariable,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var text = Resolve(values, option, environmentVariable, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < minimum || value > maximum)
        {
            throw new ArgumentException($"{option} must be an integer from {minimum} to {maximum}.");
        }
        return value;
    }

    private static double ParseDouble(
        Dictionary<string, string> values,
        string option,
        string environmentVariable,
        double defaultValue,
        double minimum,
        double maximum)
    {
        var text = Resolve(values, option, environmentVariable, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentException($"{option} must be a finite number from {minimum} to {maximum}.");
        }
        return value;
    }

    private static bool ParseBool(
        Dictionary<string, string> values,
        string option,
        string environmentVariable,
        bool defaultValue)
    {
        var text = Resolve(values, option, environmentVariable, defaultValue.ToString());
        if (!bool.TryParse(text, out var value))
        {
            throw new ArgumentException($"{option} must be true or false.");
        }
        return value;
    }

    private static string[] ParseList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool IsSafeHttpsEndpoint(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Port == 443 &&
        uri.AbsolutePath == "/" &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static LtrApi ParseApi(string value) =>
        value.Equals("NoSql", StringComparison.OrdinalIgnoreCase) ? LtrApi.NoSql :
        value.Equals("Gremlin", StringComparison.OrdinalIgnoreCase) ? LtrApi.Gremlin :
        value.Equals("Both", StringComparison.OrdinalIgnoreCase) ? LtrApi.Both :
        throw new ArgumentException("--api must be Both, NoSql, or Gremlin.");
}

public sealed class LtrHelpRequestedException : Exception;
