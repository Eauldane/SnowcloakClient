using Lumina.Data;
using Lumina.Data.Files;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;
using Snowcloak.Utils;
using Lumina.Data.Parsing.Tex.Buffers;
using System.Runtime.InteropServices;

namespace Snowcloak.Services;

public sealed class CharacterAnalyzer : DisposableMediatorSubscriberBase
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly XivDataAnalyzer _xivDataAnalyzer;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource _baseAnalysisCts = new();
    private string _lastDataHash = string.Empty;

    public CharacterAnalyzer(ILogger<CharacterAnalyzer> logger, SnowMediator mediator, FileCacheManager fileCacheManager, XivDataAnalyzer modelAnalyzer)
        : base(logger, mediator)
    {
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            _baseAnalysisCts = _baseAnalysisCts.CancelRecreate();
            var token = _baseAnalysisCts.Token;
            _ = BaseAnalysis(msg.CharacterData, token);
        });
        _fileCacheManager = fileCacheManager;
        _xivDataAnalyzer = modelAnalyzer;
    }

    public int CurrentFile { get; internal set; }
    public bool IsAnalysisRunning => _analysisCts != null;
    public int TotalFiles { get; internal set; }
    internal Dictionary<ObjectKind, Dictionary<string, FileDataEntry>> LastAnalysis { get; } = [];

    public void CancelAnalyze()
    {
        _analysisCts?.CancelDispose();
        _analysisCts = null;
    }

    public async Task ComputeAnalysis(bool print = true, bool recalculate = false)
    {
        Logger.LogDebug("=== Calculating Character Analysis ===");

        _analysisCts = _analysisCts?.CancelRecreate() ?? new();

        var cancelToken = _analysisCts.Token;

        var allFiles = LastAnalysis.SelectMany(v => v.Value.Select(d => d.Value)).ToList();
        if (allFiles.Exists(c => !c.IsComputed || recalculate))
        {
            var remaining = allFiles.Where(c => !c.IsComputed || recalculate).ToList();
            TotalFiles = remaining.Count;
            CurrentFile = 1;
            Logger.LogDebug("=== Computing {amount} remaining files ===", remaining.Count);

            Mediator.Publish(new HaltScanMessage(nameof(CharacterAnalyzer)));
            try
            {
                foreach (var file in remaining)
                {
                    Logger.LogDebug("Computing file {file}", file.FilePaths[0]);
                    await file.ComputeSizes(_fileCacheManager, cancelToken, ignoreCacheEntries: true).ConfigureAwait(false);
                    CurrentFile++;
                }

                _fileCacheManager.WriteOutFullCsv();

            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to analyze files");
            }
            finally
            {
                Mediator.Publish(new ResumeScanMessage(nameof(CharacterAnalyzer)));
            }
        }

        Mediator.Publish(new CharacterDataAnalyzedMessage());

        _analysisCts.CancelDispose();
        _analysisCts = null;

        if (print) PrintAnalysis();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        _analysisCts?.CancelDispose();
        _baseAnalysisCts.CancelDispose();
    }

    private async Task BaseAnalysis(CharacterData charaData, CancellationToken token)
    {
        if (string.Equals(charaData.DataHash.Value, _lastDataHash, StringComparison.Ordinal)) return;

        LastAnalysis.Clear();

        foreach (var obj in charaData.FileReplacements)
        {
            Dictionary<string, FileDataEntry> data = new(StringComparer.OrdinalIgnoreCase);
            foreach (var fileEntry in obj.Value)
            {
                token.ThrowIfCancellationRequested();

                var fileCacheEntries = _fileCacheManager.GetAllFileCachesByHash(fileEntry.Hash, ignoreCacheEntries: true, validate: false).ToList();
                if (fileCacheEntries.Count == 0) continue;

                var filePath = fileCacheEntries[0].ResolvedFilepath;
                FileInfo fi = new(filePath);
                string ext = "unk?";
                try
                {
                    ext = fi.Extension[1..];
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not identify extension for {path}", filePath);
                }

                var tris = await Task.Run(() => _xivDataAnalyzer.GetTrianglesByHash(fileEntry.Hash)).ConfigureAwait(false);

                foreach (var entry in fileCacheEntries)
                {
                    data[fileEntry.Hash] = new FileDataEntry(fileEntry.Hash, ext,
                        [.. fileEntry.GamePaths],
                        fileCacheEntries.Select(c => c.ResolvedFilepath).Distinct(StringComparer.Ordinal).ToList(),
                        entry.Size > 0 ? entry.Size.Value : 0,
                        entry.CompressedSize > 0 ? entry.CompressedSize.Value : 0,
                        tris);
                }
            }

            LastAnalysis[obj.Key] = data;
        }

        Mediator.Publish(new CharacterDataAnalyzedMessage());

        _lastDataHash = charaData.DataHash.Value;
    }

    private void PrintAnalysis()
    {
        if (LastAnalysis.Count == 0) return;
        foreach (var kvp in LastAnalysis)
        {
            int fileCounter = 1;
            int totalFiles = kvp.Value.Count;
            Logger.LogInformation("=== Analysis for {obj} ===", kvp.Key);

            foreach (var entry in kvp.Value.OrderBy(b => b.Value.GamePaths.OrderBy(p => p, StringComparer.Ordinal).First(), StringComparer.Ordinal))
            {
                Logger.LogInformation("File {x}/{y}: {hash}", fileCounter++, totalFiles, entry.Key);
                foreach (var path in entry.Value.GamePaths)
                {
                    Logger.LogInformation("  Game Path: {path}", path);
                }
                if (entry.Value.FilePaths.Count > 1) Logger.LogInformation("  Multiple fitting files detected for {key}", entry.Key);
                foreach (var filePath in entry.Value.FilePaths)
                {
                    Logger.LogInformation("  File Path: {path}", filePath);
                }
                Logger.LogInformation("  Size: {size}, Compressed: {compressed}", UiSharedService.ByteToString(entry.Value.OriginalSize),
                    UiSharedService.ByteToString(entry.Value.CompressedSize));
            }
        }
        foreach (var kvp in LastAnalysis)
        {
            Logger.LogInformation("=== Detailed summary by file type for {obj} ===", kvp.Key);
            foreach (var entry in kvp.Value.Select(v => v.Value).GroupBy(v => v.FileType, StringComparer.Ordinal))
            {
                Logger.LogInformation("{ext} files: {count}, size extracted: {size}, size compressed: {sizeComp}", entry.Key, entry.Count(),
                    UiSharedService.ByteToString(entry.Sum(v => v.OriginalSize)), UiSharedService.ByteToString(entry.Sum(v => v.CompressedSize)));
            }
            Logger.LogInformation("=== Total summary for {obj} ===", kvp.Key);
            Logger.LogInformation("Total files: {count}, size extracted: {size}, size compressed: {sizeComp}", kvp.Value.Count,
            UiSharedService.ByteToString(kvp.Value.Sum(v => v.Value.OriginalSize)), UiSharedService.ByteToString(kvp.Value.Sum(v => v.Value.CompressedSize)));
        }

        Logger.LogInformation("=== Total summary for all currently present objects ===");
        Logger.LogInformation("Total files: {count}, size extracted: {size}, size compressed: {sizeComp}",
            LastAnalysis.Values.Sum(v => v.Values.Count),
            UiSharedService.ByteToString(LastAnalysis.Values.Sum(c => c.Values.Sum(v => v.OriginalSize))),
            UiSharedService.ByteToString(LastAnalysis.Values.Sum(c => c.Values.Sum(v => v.CompressedSize))));
        Logger.LogInformation("IMPORTANT NOTES:\n\r- For uploads and downloads only the compressed size is relevant.\n\r- An unusually high total files count beyond 200 and up will also increase your download time to others significantly.");
    }

    internal sealed record FileDataEntry
    {
        private readonly Lazy<TextureTraits?> _textureTraits;

        public FileDataEntry(string hash, string fileType, List<string> gamePaths, List<string> filePaths, long originalSize, long compressedSize, long triangles)
        {
            Hash = hash;
            FileType = fileType;
            GamePaths = gamePaths;
            FilePaths = filePaths;
            OriginalSize = originalSize;
            CompressedSize = compressedSize;
            Triangles = triangles;
            _textureTraits = string.Equals(fileType, "tex", StringComparison.OrdinalIgnoreCase)
                ? new Lazy<TextureTraits?>(AnalyzeTexture)
                : new Lazy<TextureTraits?>(() => null);
            Format = new Lazy<string>(() =>
            {
                if (!string.Equals(FileType, "tex", StringComparison.OrdinalIgnoreCase)) return string.Empty;

                var traits = TextureTraits;
                return traits != null ? traits.FormatSummary : "Unknown";
            });
        }

        public string Hash { get; }
        public string FileType { get; }
        public List<string> GamePaths { get; }
        public List<string> FilePaths { get; }

        public bool IsComputed => OriginalSize > 0 && CompressedSize > 0;
        public async Task ComputeSizes(FileCacheManager fileCacheManager, CancellationToken token, bool ignoreCacheEntries = true)
        {
            var compressedsize = await fileCacheManager.GetCompressedFileData(Hash, token).ConfigureAwait(false);
            var normalSize = new FileInfo(FilePaths[0]).Length;
            var entries = fileCacheManager.GetAllFileCachesByHash(Hash, ignoreCacheEntries: ignoreCacheEntries, validate: false);
            foreach (var entry in entries)
            {
                entry.Size = normalSize;
                entry.CompressedSize = compressedsize.Item2.LongLength;
            }
            OriginalSize = normalSize;
            CompressedSize = compressedsize.Item2.LongLength;
        }
        public long OriginalSize { get; private set; }
        public long CompressedSize { get; private set; }
        public long Triangles { get; private set; }
        public Lazy<string> Format { get; }
        public TextureTraits? TextureTraits => _textureTraits.Value;
        public bool IsRiskyTexture => TextureTraits?.IsRisky ?? false;

        private TextureTraits? AnalyzeTexture()
        {
            try
            {
                using var stream = new FileStream(FilePaths[0], FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new LuminaBinaryReader(stream);

                var header = reader.ReadStructure<TexFile.TexHeader>();
                var buffer = TextureBuffer.FromStream(header, reader);

                var rawData = buffer.RawData;
                var mipAllocations = buffer.MipmapAllocations;
                var mip0Length = mipAllocations != null && mipAllocations.Length > 0 ? mipAllocations[0] : rawData.Length;
                mip0Length = Math.Min(mip0Length, rawData.Length);

                var mip0Span = rawData.AsSpan(0, mip0Length);
                return TextureTraits.Create(header.Format, header.Width, header.Height, mip0Span, GamePaths);
            }
            catch
            {
                return null;
            }
        }
    }

    internal sealed record TextureTraits(
        TexFile.TextureFormat Format,
        ushort Width,
        ushort Height,
        bool HasAlpha,
        double RedVariance,
        double GreenVariance,
        double BlueVariance,
        double AverageChannelSpread,
        float AlphaTransitionDensity,
        bool HasPerceptualDarkDetailRisk,
        bool ColorsetPath)
    {
        public string FormatSummary => $"{Format} ({Width}x{Height})";
        public bool IsGreyscale => AverageChannelSpread < 2 && Math.Max(RedVariance, Math.Max(GreenVariance, BlueVariance)) < 150;
        public bool IsNormalMapStyle => !HasAlpha && BlueVariance < 50 && (RedVariance > 100 || GreenVariance > 100);
        public bool HasHighFrequencyAlpha => AlphaTransitionDensity > 0.25f && HasAlpha;
        public bool IsRisky => HasHighFrequencyAlpha || IsGreyscale || HasPerceptualDarkDetailRisk || ColorsetPath;

        public static TextureTraits Create(TexFile.TextureFormat format, ushort width, ushort height, ReadOnlySpan<byte> data, IEnumerable<string> gamePaths)
        {
            var stats = TextureChannelStatistics.Create(data);
            var alphaStats = TextureAlphaStatistics.Create(data);
            var luminanceStats = TextureLuminanceStatistics.Create(data);
            var colorsetPath = gamePaths.Any(IsColorsetOrDyePath);
            var hasPerceptualRisk = luminanceStats.LowLuminanceCoverage >= 0.35f && luminanceStats.HighDeltaDensity >= 0.2f;

            return new TextureTraits(format, width, height, alphaStats.HasAlpha, stats.RedVariance, stats.GreenVariance, stats.BlueVariance,
                stats.AverageChannelSpread, alphaStats.TransitionDensity, hasPerceptualRisk, colorsetPath);
        }

        private static bool IsColorsetOrDyePath(string path)
        {
            static bool Contains(string source, string value) => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

            return Contains(path, "colorset")
                   || Contains(path, "col")
                   || Contains(path, "_c")
                   || Contains(path, "/c")
                   || Contains(path, "dye");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct TextureChannelStatistics(double RedVariance, double GreenVariance, double BlueVariance, double AverageChannelSpread)
    {
        public static TextureChannelStatistics Create(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4) return new TextureChannelStatistics(0, 0, 0, 0);

            long sampleCount = data.Length / 4;
            int step = sampleCount > 250_000 ? (int)(sampleCount / 250_000) : 1;

            double rMean = 0, gMean = 0, bMean = 0;
            double rM2 = 0, gM2 = 0, bM2 = 0;
            double spreadSum = 0;
            long processed = 0;

            for (long i = 0; i < sampleCount; i += step)
            {
                int index = (int)(i * 4);
                byte r = data[index];
                byte g = data[index + 1];
                byte b = data[index + 2];

                processed++;
                double deltaR = r - rMean;
                rMean += deltaR / processed;
                rM2 += deltaR * (r - rMean);

                double deltaG = g - gMean;
                gMean += deltaG / processed;
                gM2 += deltaG * (g - gMean);

                double deltaB = b - bMean;
                bMean += deltaB / processed;
                bM2 += deltaB * (b - bMean);

                int spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                spreadSum += spread;
            }
            double varianceDivisor = Math.Max(1, processed - 1);
            return new TextureChannelStatistics(rM2 / varianceDivisor, gM2 / varianceDivisor, bM2 / varianceDivisor, spreadSum / processed);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct TextureAlphaStatistics(bool HasAlpha, float TransitionDensity)
    {
        public static TextureAlphaStatistics Create(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4) return new TextureAlphaStatistics(false, 0);

            long pixelCount = data.Length / 4;
            int step = pixelCount > 500_000 ? (int)(pixelCount / 500_000) : 1;

            byte? prevAlpha = null;
            long transitions = 0;
            long processed = 0;
            byte minAlpha = byte.MaxValue;
            byte maxAlpha = byte.MinValue;

            for (long i = 0; i < pixelCount; i += step)
            {
                int alphaIndex = (int)(i * 4 + 3);
                byte alpha = data[alphaIndex];
                processed++;
                minAlpha = Math.Min(minAlpha, alpha);
                maxAlpha = Math.Max(maxAlpha, alpha);

                if (prevAlpha.HasValue && Math.Abs(alpha - prevAlpha.Value) > 32)
                {
                    transitions++;
                }
                prevAlpha = alpha;
            }

            bool hasAlpha = minAlpha < 250 && maxAlpha > 0;
            float density = processed > 0 ? transitions / (float)processed : 0f;
            return new TextureAlphaStatistics(hasAlpha, density);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct TextureLuminanceStatistics(float LowLuminanceCoverage, float HighDeltaDensity)
    {
        public static TextureLuminanceStatistics Create(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4) return new TextureLuminanceStatistics(0, 0);

            long sampleCount = data.Length / 4;
            int step = sampleCount > 250_000 ? (int)(sampleCount / 250_000) : 1;

            double? previousLuma = null;
            long lowLuminanceSamples = 0;
            long highDeltaSamples = 0;
            long processed = 0;

            for (long i = 0; i < sampleCount; i += step)
            {
                int index = (int)(i * 4);
                byte r = data[index];
                byte g = data[index + 1];
                byte b = data[index + 2];

                double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                if (luma < 64)
                {
                    lowLuminanceSamples++;
                }

                if (previousLuma.HasValue && Math.Abs(luma - previousLuma.Value) > 3)
                {
                    highDeltaSamples++;
                }

                previousLuma = luma;
                processed++;
            }

            if (processed == 0) return new TextureLuminanceStatistics(0, 0);

            float lowLuminanceCoverage = lowLuminanceSamples / (float)processed;
            float highDeltaDensity = processed > 1 ? highDeltaSamples / (float)(processed - 1) : 0f;
            return new TextureLuminanceStatistics(lowLuminanceCoverage, highDeltaDensity);
        }
    }
}
