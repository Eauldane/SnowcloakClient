namespace Snowcloak.Core.Analysis;

public sealed record AnalysisTextureTraits(
    string Format,
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

    public static AnalysisTextureTraits Create(string format, ushort width, ushort height, ReadOnlySpan<byte> data, IEnumerable<string> gamePaths)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(gamePaths);

        var stats = TextureChannelStatistics.Calculate(data);
        var alphaStats = TextureAlphaStatistics.Calculate(data);
        var luminanceStats = TextureLuminanceStatistics.Calculate(data);
        var colorsetPath = gamePaths.Any(IsColorsetOrDyePath);
        var hasPerceptualRisk = luminanceStats.LowLuminanceCoverage >= 0.35f && luminanceStats.HighDeltaDensity >= 0.2f;

        return new AnalysisTextureTraits(format, width, height, alphaStats.HasAlpha, stats.RedVariance, stats.GreenVariance, stats.BlueVariance,
            stats.AverageChannelSpread, alphaStats.TransitionDensity, hasPerceptualRisk, colorsetPath);
    }

    private static bool IsColorsetOrDyePath(string path)
    {
        static bool Contains(string source, string value) => source.Contains(value, StringComparison.OrdinalIgnoreCase);

        return Contains(path, "colorset")
               || Contains(path, "col")
               || Contains(path, "_c")
               || Contains(path, "/c")
               || Contains(path, "dye");
    }

    private readonly record struct TextureChannelStatistics(double RedVariance, double GreenVariance, double BlueVariance, double AverageChannelSpread)
    {
        public static TextureChannelStatistics Calculate(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4) return new TextureChannelStatistics(0, 0, 0, 0);

            var sampleCount = data.Length / 4;
            var step = sampleCount > 250_000 ? sampleCount / 250_000 : 1;

            double rMean = 0, gMean = 0, bMean = 0;
            double rM2 = 0, gM2 = 0, bM2 = 0;
            double spreadSum = 0;
            var processed = 0L;

            for (var i = 0; i < sampleCount; i += step)
            {
                var index = i * 4;
                var r = data[index];
                var g = data[index + 1];
                var b = data[index + 2];

                processed++;
                var deltaR = r - rMean;
                rMean += deltaR / processed;
                rM2 += deltaR * (r - rMean);

                var deltaG = g - gMean;
                gMean += deltaG / processed;
                gM2 += deltaG * (g - gMean);

                var deltaB = b - bMean;
                bMean += deltaB / processed;
                bM2 += deltaB * (b - bMean);

                var spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                spreadSum += spread;
            }

            var varianceDivisor = Math.Max(1, processed - 1);
            return new TextureChannelStatistics(rM2 / varianceDivisor, gM2 / varianceDivisor, bM2 / varianceDivisor, spreadSum / processed);
        }
    }

    private readonly record struct TextureAlphaStatistics(bool HasAlpha, float TransitionDensity)
    {
        public static TextureAlphaStatistics Calculate(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4) return new TextureAlphaStatistics(false, 0);

            var pixelCount = data.Length / 4;
            var step = pixelCount > 500_000 ? pixelCount / 500_000 : 1;

            byte? previousAlpha = null;
            var transitions = 0L;
            var processed = 0L;
            var minAlpha = byte.MaxValue;
            var maxAlpha = byte.MinValue;

            for (var i = 0; i < pixelCount; i += step)
            {
                var alphaIndex = i * 4 + 3;
                var alpha = data[alphaIndex];
                processed++;
                minAlpha = Math.Min(minAlpha, alpha);
                maxAlpha = Math.Max(maxAlpha, alpha);

                if (previousAlpha.HasValue && Math.Abs(alpha - previousAlpha.Value) > 32)
                {
                    transitions++;
                }
                previousAlpha = alpha;
            }

            var hasAlpha = minAlpha < 250 && maxAlpha > 0;
            var density = processed > 0 ? transitions / (float)processed : 0f;
            return new TextureAlphaStatistics(hasAlpha, density);
        }
    }

    private readonly record struct TextureLuminanceStatistics(float LowLuminanceCoverage, float HighDeltaDensity)
    {
        public static TextureLuminanceStatistics Calculate(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4) return new TextureLuminanceStatistics(0, 0);

            var sampleCount = data.Length / 4;
            var step = sampleCount > 250_000 ? sampleCount / 250_000 : 1;

            double? previousLuma = null;
            var lowLuminanceSamples = 0L;
            var highDeltaSamples = 0L;
            var processed = 0L;

            for (var i = 0; i < sampleCount; i += step)
            {
                var index = i * 4;
                var r = data[index];
                var g = data[index + 1];
                var b = data[index + 2];

                var luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
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

            var lowLuminanceCoverage = lowLuminanceSamples / (float)processed;
            var highDeltaDensity = processed > 1 ? highDeltaSamples / (float)(processed - 1) : 0f;
            return new TextureLuminanceStatistics(lowLuminanceCoverage, highDeltaDensity);
        }
    }
}
