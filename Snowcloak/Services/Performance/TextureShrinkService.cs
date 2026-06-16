using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.FileCache;

namespace Snowcloak.Services.Performance;

public sealed partial class TextureShrinkService
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly ILogger<TextureShrinkService> _logger;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly XivDataAnalyzer _xivDataAnalyzer;

    public TextureShrinkService(
        ILogger<TextureShrinkService> logger,
        PlayerPerformanceConfigService playerPerformanceConfigService,
        FileCacheManager fileCacheManager,
        XivDataAnalyzer xivDataAnalyzer)
    {
        _logger = logger;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _fileCacheManager = fileCacheManager;
        _xivDataAnalyzer = xivDataAnalyzer;
    }

    public async Task<bool> ShrinkTextures(CharacterData charaData, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(charaData);

        var config = _playerPerformanceConfigService.Current;
        if (config.TextureShrinkMode is TextureShrinkMode.Never or TextureShrinkMode.Default)
        {
            return false;
        }

        var textureHashes = charaData.FileReplacements
            .SelectMany(entry => entry.Value)
            .Where(replacement => string.IsNullOrEmpty(replacement.FileSwapPath)
                && replacement.GamePaths.Any(path => path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)))
            .Select(replacement => replacement.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var shrunken = false;
        await Parallel.ForEachAsync(textureHashes, token, async (hash, cancellationToken) =>
        {
            var fileEntry = _fileCacheManager.GetFileCacheByHash(hash, preferSubst: true);
            if (fileEntry == null || fileEntry.IsSubstEntry)
            {
                return;
            }

            var texFormat = _xivDataAnalyzer.GetTexFormatByHash(hash);
            var resize = TextureResizePlan.Create(texFormat.Width, texFormat.Height, texFormat.Format, texFormat.MipCount);
            if (!resize.ShouldShrink)
            {
                return;
            }

            var filePath = fileEntry.ResolvedFilepath;
            var tempPath = _fileCacheManager.GetSubstFilePath(Guid.NewGuid().ToString(), "tmp");
            var substitutePath = _fileCacheManager.GetSubstFilePath(hash, "tex");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath) ?? _fileCacheManager.SubstFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(substitutePath) ?? _fileCacheManager.SubstFolder);

            LogShrinkingTexture(_logger, hash, texFormat.Width, texFormat.Height, resize.Width, resize.Height);

            try
            {
                await RewriteTextureAsync(filePath, tempPath, resize, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, substitutePath, overwrite: true);
                var substituteEntry = _fileCacheManager.CreateSubstEntry(substitutePath);
                if (substituteEntry != null)
                {
                    substituteEntry.CompressedSize = fileEntry.CompressedSize;
                }

                shrunken = true;
                DeleteOriginalIfConfigured(fileEntry, filePath);
            }
            catch (IOException ex)
            {
                LogFailedToShrinkTexture(_logger, ex, hash);
                TryDeleteTemp(tempPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogFailedToShrinkTexture(_logger, ex, hash);
                TryDeleteTemp(tempPath);
            }
            catch (InvalidDataException ex)
            {
                LogFailedToShrinkTexture(_logger, ex, hash);
                TryDeleteTemp(tempPath);
            }
            catch (ArgumentException ex)
            {
                LogFailedToShrinkTexture(_logger, ex, hash);
                TryDeleteTemp(tempPath);
            }
            catch (NotSupportedException ex)
            {
                LogFailedToShrinkTexture(_logger, ex, hash);
                TryDeleteTemp(tempPath);
            }
            catch (ObjectDisposedException ex)
            {
                LogFailedToShrinkTexture(_logger, ex, hash);
                TryDeleteTemp(tempPath);
            }
        }).ConfigureAwait(false);

        return shrunken;
    }

    private void DeleteOriginalIfConfigured(FileCacheEntity fileEntry, string filePath)
    {
        if (!_playerPerformanceConfigService.Current.TextureShrinkDeleteOriginal
            || !fileEntry.IsCacheEntry
            || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            LogDeletingOriginalTexture(_logger, filePath);
            File.Delete(filePath);
        }
        catch (IOException ex)
        {
            LogFailedToDeleteOriginalTexture(_logger, ex, filePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogFailedToDeleteOriginalTexture(_logger, ex, filePath);
        }
    }

    private static async Task RewriteTextureAsync(string sourcePath, string tempPath, TextureResizePlan resize, CancellationToken token)
    {
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        var header = reader.ReadBytes(80);
        reader.BaseStream.Position = 14;
        var mipByte = reader.ReadByte();
        var mipCount = (byte)(mipByte & 0x7F);

        using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(header);

        writer.BaseStream.Position = 8;
        writer.Write((ushort)resize.Width);
        writer.Write((ushort)resize.Height);

        writer.BaseStream.Position = 14;
        writer.Write((ushort)((mipByte & 0x80) | (mipCount - resize.MipLevel)));

        writer.BaseStream.Position = 16;
        for (var index = 0; index < 3; index++)
        {
            writer.Write(0U);
        }

        writer.BaseStream.Position = 28;
        for (var index = 0; index < 13; index++)
        {
            writer.Write(80U);
        }

        output.Position = 80;
        input.Position = 80 + resize.OffsetDelta;

        await input.CopyToAsync(output, 81920, token).ConfigureAwait(false);
    }

    private void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            LogFailedToDeleteTemporaryTextureFile(_logger, ex, path);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogFailedToDeleteTemporaryTextureFile(_logger, ex, path);
        }
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Shrinking {Hash} from {SourceWidth}x{SourceHeight} to {TargetWidth}x{TargetHeight}")]
    private static partial void LogShrinkingTexture(ILogger logger, string hash, uint sourceWidth, uint sourceHeight, uint targetWidth, uint targetHeight);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to shrink texture {Hash}")]
    private static partial void LogFailedToShrinkTexture(ILogger logger, Exception exception, string hash);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Deleting original texture: {FilePath}")]
    private static partial void LogDeletingOriginalTexture(ILogger logger, string filePath);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Failed to delete original texture {FilePath}")]
    private static partial void LogFailedToDeleteOriginalTexture(ILogger logger, Exception exception, string filePath);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Failed to delete temporary texture file {FilePath}")]
    private static partial void LogFailedToDeleteTemporaryTextureFile(ILogger logger, Exception exception, string filePath);
}

internal readonly record struct TextureResizePlan(bool ShouldShrink, uint Width, uint Height, int MipLevel, long OffsetDelta)
{
    public static TextureResizePlan Create(uint sourceWidth, uint sourceHeight, uint format, int mipCount)
    {
        var bitsPerPixel = BitsPerPixel(format);
        if (bitsPerPixel == 0 || mipCount <= 1)
        {
            return default;
        }

        var maxPixels = bitsPerPixel <= 8 ? 2048U * 2048U : 1024U * 1024U;
        var width = sourceWidth;
        var height = sourceHeight;
        var mipLevel = 0;
        var offsetDelta = 0L;

        while (width * height > maxPixels && mipLevel < mipCount - 1)
        {
            offsetDelta += width * height * bitsPerPixel / 8;
            mipLevel++;
            width /= 2;
            height /= 2;
        }

        return offsetDelta == 0
            ? default
            : new TextureResizePlan(true, width, height, mipLevel, offsetDelta);
    }

    private static uint BitsPerPixel(uint format)
    {
        return format switch
        {
            0x1130 => 8,
            0x1131 => 8,
            0x1440 => 16,
            0x1441 => 16,
            0x1450 => 32,
            0x1451 => 32,
            0x2150 => 32,
            0x2250 => 32,
            0x2260 => 64,
            0x2460 => 64,
            0x2470 => 128,
            0x3420 => 4,
            0x3430 => 8,
            0x3431 => 8,
            0x4140 => 16,
            0x4250 => 32,
            0x6120 => 4,
            0x6230 => 8,
            0x6432 => 8,
            _ => 0,
        };
    }
}
