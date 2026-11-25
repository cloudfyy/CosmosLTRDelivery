using System.Globalization;

namespace CosmosLab.Common;

public sealed class CliOptions
{
    private static readonly HashSet<string> Commands =
        ["seed", "smoke", "list", "read", "write", "delete", "help"];
    private readonly Dictionary<string, string> values;

    private CliOptions(string command, Dictionary<string, string> values)
    {
        Command = command;
        this.values = values;
    }

    public string Command { get; }

    public static CliOptions Parse(string[] arguments, params string[] supportedOptions)
    {
        if (arguments is ["--help"])
        {
            return new CliOptions("help", []);
        }
        if (arguments.Length == 0 || !Commands.Contains(arguments[0]))
        {
            throw new ArgumentException("Specify seed, smoke, list, read, write, delete, or --help.");
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Length; index += 2)
        {
            var option = arguments[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) ||
                !supportedOptions.Contains(option[2..], StringComparer.Ordinal) ||
                index + 1 >= arguments.Length ||
                string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                !options.TryAdd(option[2..], arguments[index + 1]))
            {
                throw new ArgumentException($"Unknown, duplicate, empty, or incomplete option: {option}.");
            }
        }
        return new CliOptions(arguments[0], options);
    }

    public string Required(string name) => values.TryGetValue(name, out var value)
        ? value
        : throw new ArgumentException($"--{name} is required for {Command}.");

    public string? Optional(string name) => values.GetValueOrDefault(name);

    public string Container(IReadOnlyCollection<string> allowedNames)
    {
        var name = Required("container");
        if (!allowedNames.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown container/graph '{name}'. Allowed names: {string.Join(", ", allowedNames)}.");
        }
        return name;
    }

    public int Limit()
    {
        var text = Optional("limit") ?? "5";
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) ||
            limit is < 1 or > 10)
        {
            throw new ArgumentException("--limit must be an integer from 1 to 10.");
        }
        return limit;
    }

    public string Identifier(string name)
    {
        var value = Required(name);
        LabConfiguration.ValidateIdentifier(value, name);
        return value;
    }
}
