namespace Snowcloak.Core.Analysis;

public sealed record AnalysisFileEntry
{
    public AnalysisFileEntry(
        string hash,
        string fileType,
        IEnumerable<string> gamePaths,
        IEnumerable<string> filePaths,
        long originalSize,
        long compressedSize,
        long triangles,
        AnalysisTextureTraits? textureTraits = null)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(fileType);
        ArgumentNullException.ThrowIfNull(gamePaths);
        ArgumentNullException.ThrowIfNull(filePaths);

        Hash = hash;
        FileType = fileType;
        GamePaths = Array.AsReadOnly(gamePaths.ToArray());
        FilePaths = Array.AsReadOnly(filePaths.ToArray());
        OriginalSize = originalSize;
        CompressedSize = compressedSize;
        Triangles = triangles;
        TextureTraits = textureTraits;
    }

    public string Hash { get; }
    public string FileType { get; }
    public IReadOnlyList<string> GamePaths { get; }
    public IReadOnlyList<string> FilePaths { get; }
    public long OriginalSize { get; }
    public long CompressedSize { get; }
    public long Triangles { get; }
    public AnalysisTextureTraits? TextureTraits { get; }
    public bool IsComputed => OriginalSize > 0 && CompressedSize > 0;
    public string FormatSummary => TextureTraits?.FormatSummary ?? string.Empty;
    public bool IsRiskyTexture => TextureTraits?.IsRisky ?? false;

    public AnalysisFileEntry WithSizes(long originalSize, long compressedSize)
        => new(Hash, FileType, GamePaths, FilePaths, originalSize, compressedSize, Triangles, TextureTraits);
}
