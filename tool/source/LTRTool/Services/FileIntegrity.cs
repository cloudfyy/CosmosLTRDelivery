using System.Security.Cryptography;

namespace CosmosLab.LTRTool;

public static class FileIntegrity
{
    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    public static async Task<LocalRecoveryFile> CreateRecordAsync(
        string relativePath,
        string workRoot,
        long itemCount,
        double requestCharge,
        CancellationToken cancellationToken = default)
    {
        var localPath = Path.Combine(workRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(localPath);
        return new LocalRecoveryFile(
            relativePath,
            localPath,
            itemCount,
            info.Length,
            await ComputeSha256Async(localPath, cancellationToken),
            requestCharge);
    }
}
