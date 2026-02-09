using System.Collections.Immutable;
using Snowcloak.Files;

namespace Snowcloak.FileCache;

public static class SupportedFileTypes
{
    private static readonly ImmutableDictionary<string, FileExtension> ExtensionMap =
        new Dictionary<string, FileExtension>(StringComparer.OrdinalIgnoreCase)
        {
            [".mdl"] = FileExtension.MDL,
            [".tex"] = FileExtension.TEX,
            [".mtrl"] = FileExtension.MTRL,
            [".tmb"] = FileExtension.TMB,
            [".pap"] = FileExtension.PAP,
            [".avfx"] = FileExtension.AVFX,
            [".atex"] = FileExtension.ATEX,
            [".sklb"] = FileExtension.SKLB,
            [".eid"] = FileExtension.EID,
            [".phyb"] = FileExtension.PHYB,
            [".pbd"] = FileExtension.PBD,
            [".scd"] = FileExtension.SCD,
            [".skp"] = FileExtension.SKP,
            [".shpk"] = FileExtension.SHPK,
            [".dds"] = FileExtension.DDS,
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static IImmutableSet<string> AllowedFileExtensions { get; } =
        ExtensionMap.Keys.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowedPath(string path) =>
        AllowedFileExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public static FileExtension ParseFileExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) throw new ArgumentNullException(nameof(extension));

        var normalized = extension.Trim();
        if (!normalized.StartsWith('.')) normalized = "." + normalized;

        if (ExtensionMap.TryGetValue(normalized, out var result)) return result;

        throw new NotSupportedException($"Unsupported extension: {extension}");
    }
}
