using Snowcloak.API.Data;
using Snowcloak.API.Dto.Files;
using Snowcloak.API.Routes;
using Microsoft.Extensions.Logging;
using Snowcloak.CacheFile;
using Snowcloak.Core.Files;
using Snowcloak.Core.IO;
using Snowcloak.FileCache;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI.Files.Models;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;

namespace Snowcloak.WebAPI.Files;

public sealed partial class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private const int DownloadBufferSize = 256 * 1024;
    private const long DownloadProgressReportByteInterval = 256 * 1024;
    private static readonly TimeSpan DownloadProgressReportMinInterval = TimeSpan.FromMilliseconds(100);

    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly IFileDownloadTransport _transport;
    private readonly DownloadStatusStore _statusStore;
    private readonly UsageStatisticsService _usageStatisticsService;
    private readonly ConcurrentDictionary<ThrottledStream, byte> _activeDownloadStreams = new();
    private List<DownloadFileTransfer> _currentDownloads = [];

    public FileDownloadManager(ILogger<FileDownloadManager> logger, SnowMediator mediator,
        FileTransferOrchestrator orchestrator, IFileDownloadTransport transport,
        DownloadStatusStore statusStore, FileCacheManager fileCacheManager, UsageStatisticsService usageStatisticsService) : base(logger, mediator)
    {
        _orchestrator = orchestrator;
        _transport = transport;
        _statusStore = statusStore;
        _fileDbManager = fileCacheManager;
        _usageStatisticsService = usageStatisticsService;

        Mediator.Subscribe<DownloadLimitChangedMessage>(this, (msg) =>
        {
            if (_activeDownloadStreams.IsEmpty) return;
            var newLimit = _orchestrator.DownloadLimitPerSlot();
            Logger.LogTrace("Setting new Download Speed Limit to {newLimit}", newLimit);
            foreach (var stream in _activeDownloadStreams.Keys)
            {
                stream.BandwidthLimit = newLimit;
            }
        });
    }

    public bool IsHashForbidden(string hash) => _orchestrator.IsForbidden(hash);

    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        Logger.LogDebug("Download start: {id}", gameObjectHandler.Name);

        List<DownloadFileDto> downloadFileInfoFromService =
        [
            .. await FilesGetSizes(fileReplacement.Select(f => f.Hash).Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false),
        ];

        Logger.LogDebug("Files with size 0 or less: {files}", string.Join(", ", downloadFileInfoFromService.Where(f => f.Size <= 0).Select(f => f.Hash)));

        foreach (var dto in downloadFileInfoFromService.Where(c => c.IsForbidden))
        {
            _orchestrator.AddForbiddenTransfer(new ForbiddenTransfer(dto.Hash, dto.ForbiddenBy, ForbiddenTransferKind.Download));
        }

        _currentDownloads = downloadFileInfoFromService.Distinct()
            .Select(d => new DownloadFileTransfer(d))
            .Where(d => d.CanBeTransferred)
            .ToList();

        return _currentDownloads;
    }

    public async Task DownloadFiles(GameObjectHandler gameObject, List<FileReplacementData> fileReplacementDto, CancellationToken ct, string? uid = null)
    {
        Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles)));
        try
        {
            await DownloadFilesInternal(gameObject, fileReplacementDto, ct, uid).ConfigureAwait(false);
        }
        finally
        {
            Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        foreach (var stream in _activeDownloadStreams.Keys)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogTrace(ex, "Error disposing active download stream");
            }
        }

        _activeDownloadStreams.Clear();
        base.Dispose(disposing);
    }

    private async Task DownloadFilesInternal(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct, string? uid)
    {
        var downloadType = _orchestrator.PreferredDownloadTypeQueryValue();
        var downloadGroups = _currentDownloads
            .GroupBy(f => f.DownloadUri.Host + ":" + f.DownloadUri.Port, StringComparer.Ordinal)
            .ToList();

        var expectedExtensionByHash = fileReplacement
            .GroupBy(replacement => replacement.Hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().GamePaths[0].Split(".")[^1], StringComparer.OrdinalIgnoreCase);

        using var download = _statusStore.Begin(gameObjectHandler, uid);
        var groupHandles = new Dictionary<string, DownloadStatusStore.DownloadGroupHandle>(StringComparer.Ordinal);
        foreach (var group in downloadGroups)
        {
            groupHandles[group.Key] = download.AddGroup(group.Key,
                totalBytes: group.Sum(c => BlockFileFormat.EntryLength(c.Hash, c.Total)),
                totalFiles: 1);
        }

        await Parallel.ForEachAsync(downloadGroups, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, downloadGroups.Count),
            CancellationToken = ct,
        },
        async (fileGroup, token) =>
        {
            var groupHandle = groupHandles[fileGroup.Key];
            var transfers = fileGroup.ToList();
            var request = new DownloadGroupRequest(transfers[0].DownloadUri, transfers.Select(t => t.Hash).ToList(), downloadType);
            var blockFile = _fileDbManager.GetTemporaryCacheFilePath(Guid.NewGuid().ToString("N"), "blk");

            try
            {
                groupHandle.SetStatus(DownloadStatus.WaitingForSlot);
                await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
                try
                {
                    await DownloadGroupToBlockFileAsync(request, groupHandle, blockFile, token).ConfigureAwait(false);
                    var downloadedBytes = GetFileLength(blockFile);
                    await ExtractBlockFileAsync(blockFile, groupHandle, expectedExtensionByHash, token).ConfigureAwait(false);
                    _usageStatisticsService.RecordDownloadedBytes(downloadedBytes);
                }
                finally
                {
                    _orchestrator.ReleaseDownloadSlot();
                }
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("Download cancelled for {id} on {server}", gameObjectHandler.Name, fileGroup.Key);
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error during download of {id} on {server}", gameObjectHandler.Name, fileGroup.Key);
            }
            finally
            {
                TryDeleteFile(blockFile);
            }
        }).ConfigureAwait(false);

        Logger.LogDebug("Download end: {id}", gameObjectHandler.Name);
    }

    private static long GetFileLength(string path)
    {
        var fileInfo = new FileInfo(path);
        return fileInfo.Exists ? fileInfo.Length : 0;
    }

    private async Task DownloadGroupToBlockFileAsync(DownloadGroupRequest request, DownloadStatusStore.DownloadGroupHandle groupHandle, string blockFile, CancellationToken ct)
    {
        var download = await _transport.OpenAsync(request, groupHandle.SetStatus, ct).ConfigureAwait(false);
        await using (download.ConfigureAwait(false))
        {
            if (download.ReportedTotalBytes is long reportedTotal)
            {
                groupHandle.SetTotalBytes(reportedTotal);
            }

            var directory = Path.GetDirectoryName(blockFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var fileStream = File.Create(blockFile);
            await using (fileStream.ConfigureAwait(false))
            {
                var limit = _orchestrator.DownloadLimitPerSlot();
                LogStartingDownload(Logger, limit, blockFile);

                var throttledStream = new ThrottledStream(download.Stream, limit);
                _activeDownloadStreams.TryAdd(throttledStream, 0);
                try
                {
                    await CopyToBlockFileAsync(throttledStream, fileStream, groupHandle, ct).ConfigureAwait(false);
                }
                finally
                {
                    _activeDownloadStreams.TryRemove(throttledStream, out _);
                    await throttledStream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task CopyToBlockFileAsync(Stream source, Stream destination, DownloadStatusStore.DownloadGroupHandle groupHandle, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
        long pendingProgressBytes = 0;
        var lastProgressReportTimestamp = Stopwatch.GetTimestamp();
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, DownloadBufferSize), ct).ConfigureAwait(false)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                pendingProgressBytes += bytesRead;

                var currentTimestamp = Stopwatch.GetTimestamp();
                var byteThresholdReached = pendingProgressBytes >= DownloadProgressReportByteInterval;
                var timeThresholdReached =
                    Stopwatch.GetElapsedTime(lastProgressReportTimestamp, currentTimestamp) >= DownloadProgressReportMinInterval;

                if (!byteThresholdReached && !timeThresholdReached)
                {
                    continue;
                }

                groupHandle.AddBytes(pendingProgressBytes);
                pendingProgressBytes = 0;
                lastProgressReportTimestamp = currentTimestamp;
            }

            if (pendingProgressBytes > 0)
            {
                groupHandle.AddBytes(pendingProgressBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ExtractBlockFileAsync(string blockFile, DownloadStatusStore.DownloadGroupHandle groupHandle,
        IReadOnlyDictionary<string, string> expectedExtensionByHash, CancellationToken ct)
    {
        groupHandle.SetStatus(DownloadStatus.Decompressing);
        groupHandle.MarkFileTransferred();

        List<BlockFileEntry> entries;
        var fileBlockStream = File.OpenRead(blockFile);
        await using (fileBlockStream.ConfigureAwait(false))
        {
            entries = [.. BlockFileFormat.ReadEntries(fileBlockStream)];
        }

        foreach (var entry in entries)
        {
            if (!expectedExtensionByHash.ContainsKey(entry.Hash))
            {
                throw new InvalidDataException($"Missing expected extension metadata for {entry.Hash}.");
            }
        }

        var threadCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        await Parallel.ForEachAsync(entries, new ParallelOptions
        {
            MaxDegreeOfParallelism = threadCount,
            CancellationToken = ct,
        },
        async (entry, token) => await ExtractBlockEntryAsync(blockFile, entry, expectedExtensionByHash[entry.Hash], token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async Task ExtractBlockEntryAsync(string blockFile, BlockFileEntry entry, string expectedExtension, CancellationToken ct)
    {
        try
        {
            var chunkStream = new FileStream(blockFile, new FileStreamOptions
            {
                BufferSize = 80000,
                Mode = FileMode.Open,
                Access = FileAccess.Read,
            });
            await using (chunkStream.ConfigureAwait(false))
            {
                chunkStream.Position = entry.DataOffset;
                using var limitedStream = new LimitedStream(chunkStream, entry.Length)
                {
                    DisposeUnderlying = false
                };

                var startPosition = chunkStream.Position;
                var extractedPath = await ExtractScfChunkToCacheAsync(limitedStream, entry.Hash, expectedExtension, ct).ConfigureAwait(false);
                if (chunkStream.Position - startPosition != entry.Length)
                {
                    throw new EndOfStreamException();
                }

                LogExtracted(Logger, entry.Hash, entry.Length, extractedPath);
                PersistFileToStorage(entry.Hash, extractedPath, entry.Length);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EndOfStreamException)
        {
            Logger.LogWarning("Failure to extract file {fileHash}, stream ended prematurely", entry.Hash);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during decompression of {hash}", entry.Hash);
        }
    }

    private async Task<string> ExtractScfChunkToCacheAsync(Stream scfStream, string expectedHash, string expectedExtension, CancellationToken ct)
    {
        var chunkStart = scfStream.Position;
        var header = ScfFile.ReadHeader(scfStream);
        if (!string.Equals(header.Hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SCF hash mismatch. Expected {expectedHash}, got {header.Hash}.");
        }

        var actualExtension = header.FileExtension.ToString();
        if (!string.Equals(actualExtension, expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            LogExtractedExtensionMismatch(Logger, actualExtension, expectedExtension, expectedHash);
        }

        scfStream.Position = chunkStart;

        var tempPath = _fileDbManager.GetTemporaryCacheFilePath(expectedHash + "-" + Guid.NewGuid().ToString("N"), "tmp");
        var tempDirectory = Path.GetDirectoryName(tempPath);
        if (!string.IsNullOrEmpty(tempDirectory))
        {
            Directory.CreateDirectory(tempDirectory);
        }

        try
        {
            var output = new FileStream(tempPath, new FileStreamOptions
            {
                Access = FileAccess.Write,
                BufferSize = 128 * 1024,
                Mode = FileMode.CreateNew,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                Share = FileShare.None
            });
            await using (output.ConfigureAwait(false))
            {
                var extractedHash = await ScfFile.ExtractSCFToStream(scfStream, output, ct).ConfigureAwait(false);
                if (!string.Equals(extractedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Extracted hash mismatch. Expected {expectedHash}, got {extractedHash}.");
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            var finalPath = _fileDbManager.GetCacheFilePath(expectedHash, actualExtension);
            var finalDirectory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(finalDirectory))
            {
                Directory.CreateDirectory(finalDirectory);
            }

            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private async Task<List<DownloadFileDto>> FilesGetSizes(List<string> hashes, CancellationToken ct)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");
        using var response = await _orchestrator.SendRequestAsync(HttpMethod.Get,
            SnowFiles.ServerFilesGetSizesFullPath(_orchestrator.FilesCdnUri!, _orchestrator.PreferredDownloadTypeQueryValue()),
            hashes, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DownloadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private void PersistFileToStorage(string fileHash, string filePath, long compressedSize)
    {
        try
        {
            var entry = _fileDbManager.CreateCacheEntry(filePath, fileHash);
            if (entry != null)
            {
                entry.CompressedSize = compressedSize;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating cache entry");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not delete temporary download file {path}", path);
        }
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Starting download with a speed limit of {limit} to {tempPath}")]
    private static partial void LogStartingDownload(ILogger logger, long limit, string tempPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Extracted extension {ext} differs from expected {expectedExt} for {hash}")]
    private static partial void LogExtractedExtensionMismatch(ILogger logger, string ext, string expectedExt, string hash);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Extracted {file}:{length} => {dest}")]
    private static partial void LogExtracted(ILogger logger, string file, long length, string dest);
}
