using System.Buffers;
using System.Text;
using K4os.Compression.LZ4.Legacy;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Utils;

namespace Snowcloak.Services.CharaData;

public static partial class McdfIo
{
    public static (MareCharaFileHeader Header, long ExpectedLength) ReadHeader(string filePath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        using var unwrapped = File.OpenRead(filePath);
        using var lz4Stream = new LZ4Stream(unwrapped, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
        using var reader = new BinaryReader(lz4Stream);
        var loadedCharaFile = MareCharaFileHeader.FromBinaryReader(filePath, reader)
            ?? throw new InvalidOperationException("MCDF header was null");

        var expectedLength = loadedCharaFile.CharaFileData.Files.Sum(file => (long)file.Length);
        if (logger.IsEnabled(LogLevel.Information))
        {
            var expectedLengthText = expectedLength.ToByteString();
            LogReadMcdfHeader(logger, loadedCharaFile.Version, expectedLengthText);
        }

        return (loadedCharaFile, expectedLength);
    }

    public static Dictionary<string, string> ExtractFiles(
        MareCharaFileHeader charaFileHeader,
        long expectedLength,
        Func<string> createTempPath,
        ICollection<string> extractedFiles,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(charaFileHeader);
        ArgumentNullException.ThrowIfNull(createTempPath);
        ArgumentNullException.ThrowIfNull(extractedFiles);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);

        using var lz4Stream = new LZ4Stream(File.OpenRead(charaFileHeader.FilePath), LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
        using var reader = new BinaryReader(lz4Stream);
        MareCharaFileHeader.AdvanceReaderToData(reader);

        var totalRead = 0L;
        var gamePathToFilePath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fileData in charaFileHeader.CharaFileData.Files)
        {
            var fileName = createTempPath();
            extractedFiles.Add(fileName);
            using var fs = File.OpenWrite(fileName);
            using var writer = new BinaryWriter(fs);

            var buffer = reader.ReadBytes(fileData.Length);
            if (buffer.Length == 0)
            {
                throw new EndOfStreamException("Unexpected EOF");
            }

            writer.Write(buffer);
            foreach (var path in fileData.GamePaths)
            {
                gamePathToFilePath[path] = fileName;
            }

            totalRead += fileData.Length;
            if (logger.IsEnabled(LogLevel.Trace))
            {
                var totalReadText = totalRead.ToByteString();
                var expectedLengthText = expectedLength.ToByteString();
                LogReadMcdfBytes(logger, totalReadText, expectedLengthText);
            }
        }

        return gamePathToFilePath;
    }

    public static async Task WriteAsync(
        MareCharaFileHeader header,
        string filePath,
        Func<string, string?> resolveSourcePath,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(resolveSourcePath);
        ArgumentNullException.ThrowIfNull(logger);

        var tempFilePath = filePath + ".tmp";
        var completed = false;

        try
        {
            var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            await using (fileStream.ConfigureAwait(false))
            {
                using (var lz4 = new LZ4Stream(fileStream, LZ4StreamMode.Compress, LZ4StreamFlags.HighCompression))
                {
                    using var writer = new BinaryWriter(lz4, Encoding.UTF8, leaveOpen: true);
                    header.WriteToStream(writer);
                    writer.Flush();

                    foreach (var item in header.CharaFileData.Files)
                    {
                        var sourcePath = resolveSourcePath(item.Hash)
                            ?? throw new FileNotFoundException("MCDF source file was not found in the local cache.", item.Hash);

                        LogSavingMcdfFile(logger, item.Hash, sourcePath);
                        foreach (var path in item.GamePaths)
                        {
                            LogSavingMcdfPath(logger, path);
                        }

                        var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await using (source.ConfigureAwait(false))
                        {
                            await CopyExactlyAsync(source, lz4, item.Length).ConfigureAwait(false);
                        }
                    }

                    await lz4.FlushAsync().ConfigureAwait(false);
                }

                await fileStream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(tempFilePath, filePath, true);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                TryDeleteTemp(tempFilePath, logger);
            }
        }
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(81920, Math.Max(length, 1)));
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected EOF");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteTemp(string tempFilePath, ILogger logger)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch (IOException ex)
        {
            LogFailedToDeleteTemporaryMcdf(logger, ex, tempFilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogFailedToDeleteTemporaryMcdf(logger, ex, tempFilePath);
        }
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Information, Message = "Read MCDF version {Version}, expected length {ExpectedLength}")]
    private static partial void LogReadMcdfHeader(ILogger logger, byte version, string expectedLength);

    [LoggerMessage(EventId = 1, Level = LogLevel.Trace, Message = "Read {Read}/{Expected} MCDF bytes")]
    private static partial void LogReadMcdfBytes(ILogger logger, string read, string expected);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Saving to MCDF: {Hash}:{File}")]
    private static partial void LogSavingMcdfFile(ILogger logger, string hash, string file);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "\t{Path}")]
    private static partial void LogSavingMcdfPath(ILogger logger, string path);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Failed to delete temporary MCDF file {FilePath}")]
    private static partial void LogFailedToDeleteTemporaryMcdf(ILogger logger, Exception exception, string filePath);
}
