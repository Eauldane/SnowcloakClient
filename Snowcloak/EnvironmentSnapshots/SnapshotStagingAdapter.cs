using Snowcloak.API.Dto.Backups;
using Snowcloak.CacheFile;
using Snowcloak.CacheFile.Enums;
namespace Snowcloak.EnvironmentSnapshots;

public static class SnapshotStagingAdapter
{
    public static FileExtension Extension(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (Enum.TryParse<FileExtension>(Path.GetExtension(path).TrimStart('.'), true, out var extension) && extension != FileExtension.OPAQUE) return extension;
        return FileExtension.OPAQUE;
    }
    public static async Task EncodeAsync(string rawPath, string scfPath, IEnumerable<string> originalPaths, CancellationToken ct)
    {
        await using var raw = File.OpenRead(rawPath); await using var encoded = File.Create(scfPath);
        await ScfFile.CreateSCFFile(raw, encoded, Extension(originalPaths), ct: ct, multithreaded: false).ConfigureAwait(false);
    }
}
