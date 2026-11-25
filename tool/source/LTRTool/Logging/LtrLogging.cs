using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace CosmosLab.LTRTool;

public static class LtrLogging
{
    public static Logger Create(LtrOptions options)
    {
        if (!Enum.TryParse<LogEventLevel>(options.LogLevel, ignoreCase: true, out var minimumLevel))
        {
            throw new ArgumentException(
                "--log-level must be Verbose, Debug, Information, Warning, Error, or Fatal.");
        }
        return new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("application", "LTRTool")
            .Enrich.WithProperty("cloud", options.CloudName)
            .WriteTo.Console(new CompactJsonFormatter())
            .CreateLogger();
    }
}
