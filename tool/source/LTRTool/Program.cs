using CosmosLab.LTRTool;
using System.Text.Json;
using Serilog;

try
{
    var options = LtrOptions.Parse(args);
    using var logger = LtrLogging.Create(options);
    Log.Logger = logger;
    await using var application = new LtrApplication(options, logger);
    if (options.Command == LtrCommand.Inventory)
    {
        var result = await application.InventoryAsync();
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                operation = "inventory",
                status = result.Status,
                result
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Log.Information(
            "LTR inventory completed for {RecoverySetCount} recovery sets",
            result.RecoverySets.Length);
    }
    else
    {
        var result = await application.RunAsync();
        Log.Information("LTR export completed {@Result}", result);
    }
    return 0;
}
catch (ArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
catch (LtrHelpRequestedException)
{
    Console.WriteLine(LtrOptions.Usage);
    return 0;
}
catch (Exception error)
{
    if (Log.Logger != null)
    {
        Log.Fatal(error, "LTR operation failed");
    }
    else
    {
        Console.Error.WriteLine($"LTR operation failed: {error}");
    }
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
