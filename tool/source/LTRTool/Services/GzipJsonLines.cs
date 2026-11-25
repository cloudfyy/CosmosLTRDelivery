using System.IO.Compression;
using System.Text;

namespace CosmosLab.LTRTool;

public static class GzipJsonLines
{
    public static async Task WriteAsync(
        string path,
        IEnumerable<string> lines,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        await using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        foreach (var line in lines)
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
    }

    public static async Task<string[]> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
        }
        return [.. lines];
    }
}
