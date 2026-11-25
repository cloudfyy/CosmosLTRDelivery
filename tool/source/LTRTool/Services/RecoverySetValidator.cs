namespace CosmosLab.LTRTool;

public static class RecoverySetValidator
{
    public static async Task ValidateAsync(
        IReadOnlyList<LocalRecoveryFile> files,
        IReadOnlyList<ContainerRecoveryMetadata> containers,
        CancellationToken cancellationToken = default)
    {
        foreach (var file in files)
        {
            var info = new FileInfo(file.LocalPath);
            if (!info.Exists || info.Length != file.ByteCount)
            {
                throw new InvalidDataException(
                    $"Local file validation failed for '{file.RelativePath}'.");
            }
            var hash = await FileIntegrity.ComputeSha256Async(file.LocalPath, cancellationToken);
            if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Local hash validation failed for '{file.RelativePath}'.");
            }
        }

        foreach (var container in containers)
        {
            var prefix = $"data/{container.Database}/{container.Container}/";
            var exportedCount = files
                .Where(file => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
                .Sum(file => file.ItemCount);
            if (exportedCount != container.ItemCount)
            {
                throw new InvalidDataException(
                    $"Exported item-count validation failed for " +
                    $"'{container.Database}/{container.Container}': expected {container.ItemCount}, " +
                    $"found {exportedCount}.");
            }
        }
    }
}
