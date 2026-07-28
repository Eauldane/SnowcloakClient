using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.Files;
using Snowcloak.API.Routes;
using Snowcloak.CacheFile;
using Snowcloak.FileCache;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Infrastructure.Transfers;
using Snowcloak.Utils;
using Snowcloak.WebAPI.Files.Models;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Json;

namespace Snowcloak.WebAPI.Files;

public sealed partial class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private const int DownloadBufferSize = 256 * 1024;
    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly IFileDownloadTransport _transport;
    private readonly DownloadStatusStore _statusStore;
    private readonly UsageStatisticsService _usageStatisticsService;
    private readonly FileDownloadNegativeCache _negativeCache;
    private readonly ConcurrentDictionary<ThrottledStream, byte> _activeDownloadStreams = new();
    private List<DownloadFileTransfer> _currentDownloads = [];
    private Dictionary<string, FileDownloadNegativeEntry> _preflightUnavailable = new(StringComparer.OrdinalIgnoreCase);

    public FileDownloadManager(ILogger<FileDownloadManager> logger, SnowMediator mediator,
        FileTransferOrchestrator orchestrator, IFileDownloadTransport transport,
        DownloadStatusStore statusStore, FileCacheManager fileCacheManager, UsageStatisticsService usageStatisticsService,
        FileDownloadNegativeCache negativeCache) : base(logger, mediator)
    {
        _orchestrator = orchestrator;
        _transport = transport;
        _statusStore = statusStore;
        _fileDbManager = fileCacheManager;
        _usageStatisticsService = usageStatisticsService;
        _negativeCache = negativeCache;

        Mediator.Subscribe<DownloadLimitChangedMessage>(this, _ =>
        {
            if (_activeDownloadStreams.IsEmpty)
            {
                return;
            }

            var newLimit = _orchestrator.DownloadLimitPerSlot();
            LogDownloadLimitChanged(Logger, newLimit);
            foreach (var stream in _activeDownloadStreams.Keys)
            {
                stream.BandwidthLimit = newLimit;
            }
        });
    }

    public bool IsHashForbidden(string hash) => _orchestrator.IsForbidden(hash);

    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(GameObjectHandler gameObjectHandler,
        IReadOnlyCollection<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gameObjectHandler);
        ArgumentNullException.ThrowIfNull(fileReplacement);
        LogDownloadStart(Logger, gameObjectHandler.Name);
        var requestedHashes = fileReplacement.Select(file => file.Hash).Distinct(StringComparer.Ordinal).ToList();
        _preflightUnavailable = requestedHashes
            .Select(hash => _negativeCache.TryGet(hash, out var entry) ? entry : null)
            .Where(entry => entry != null)
            .ToDictionary(entry => entry!.Hash, entry => entry!, StringComparer.OrdinalIgnoreCase);
        var eligibleHashes = requestedHashes.Where(hash => !_preflightUnavailable.ContainsKey(hash)).ToList();
        var fileInfo = eligibleHashes.Count == 0
            ? []
            : await FilesGetSizes(eligibleHashes, ct).ConfigureAwait(false);
        var returnedHashes = fileInfo.Select(file => file.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var missingHash in eligibleHashes.Where(hash => !returnedHashes.Contains(hash)))
        {
            var entry = _negativeCache.Record(missingHash, FileDownloadNegativeReason.Missing, TimeSpan.FromMinutes(10),
                "The requested file is not available on the server. Snowcloak will check again later.");
            _preflightUnavailable[entry.Hash] = entry;
        }

        foreach (var dto in fileInfo.Where(file => file.IsForbidden))
        {
            _orchestrator.AddForbiddenTransfer(new ForbiddenTransfer(dto.Hash, dto.ForbiddenBy, ForbiddenTransferKind.Download));
            var entry = _negativeCache.Record(dto.Hash, FileDownloadNegativeReason.Rejected, TimeSpan.FromMinutes(30),
                "The requested file is blocked from transfer.");
            _preflightUnavailable[entry.Hash] = entry;
        }

        foreach (var dto in fileInfo.Where(file => !file.FileExists))
        {
            var entry = _negativeCache.Record(dto.Hash, FileDownloadNegativeReason.Missing, TimeSpan.FromMinutes(10),
                "The requested file is not available on the server. Snowcloak will check again later.");
            _preflightUnavailable[entry.Hash] = entry;
        }

        _currentDownloads = fileInfo.Distinct()
            .Select(dto => new DownloadFileTransfer(dto))
            .Where(transfer => transfer.CanBeTransferred)
            .ToList();
        return _currentDownloads;
    }

    public async Task DownloadFiles(GameObjectHandler gameObject, IReadOnlyCollection<FileReplacementData> fileReplacementDto,
        CancellationToken ct, string? uid = null)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        ArgumentNullException.ThrowIfNull(fileReplacementDto);
        Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles)));
        try
        {
            await DownloadFilesInternal(gameObject, fileReplacementDto, uid, ct).ConfigureAwait(false);
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
            catch (IOException ex)
            {
                LogDisposeError(Logger, ex);
            }
        }

        _activeDownloadStreams.Clear();
        base.Dispose(disposing);
    }

    private async Task DownloadFilesInternal(GameObjectHandler gameObjectHandler, IReadOnlyCollection<FileReplacementData> replacements,
        string? uid, CancellationToken ct)
    {
        var expectedExtensionByHash = replacements
            .GroupBy(replacement => replacement.Hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().GamePaths[0].Split('.')[^1], StringComparer.OrdinalIgnoreCase);
        var downloadGroups = _currentDownloads.GroupBy(GetDownloadGroupKey, StringComparer.Ordinal).ToList();

        using var download = _statusStore.Begin(gameObjectHandler, uid);
        var unavailable = new ConcurrentDictionary<string, FileDownloadNegativeEntry>(_preflightUnavailable, StringComparer.OrdinalIgnoreCase);
        if (!unavailable.IsEmpty)
        {
            var message = unavailable.Values.FirstOrDefault(entry => entry.Reason != FileDownloadNegativeReason.Missing)?.Message
                ?? string.Empty;
            download.AddGroup("Unavailable", 0, unavailable.Count).SetUnavailable(message);
        }
        var groupHandles = downloadGroups.ToDictionary(group => group.Key,
            group => download.AddGroup(group.Key, group.Sum(transfer => transfer.Total), group.Count()),
            StringComparer.Ordinal);

        await Parallel.ForEachAsync(_currentDownloads, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _currentDownloads.Count),
            CancellationToken = ct,
        }, async (transfer, token) =>
        {
            var groupHandle = groupHandles[GetDownloadGroupKey(transfer)];
            var tempPath = _fileDbManager.GetTemporaryCacheFilePath(Guid.NewGuid().ToString("N"), "scf");
            try
            {
                groupHandle.SetStatus(DownloadStatus.WaitingForSlot);
                await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
                try
                {
                    await DownloadAndExtractAsync(transfer, expectedExtensionByHash[transfer.Hash], groupHandle, tempPath, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _orchestrator.ReleaseDownloadSlot();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileDownloadUnavailableException ex)
            {
                unavailable.TryAdd(transfer.Hash, ex.Entry);
                groupHandle.SetUnavailable(ex.Entry.Reason == FileDownloadNegativeReason.Missing ? string.Empty : ex.Entry.Message);
                LogDownloadError(Logger, ex, transfer.Hash, gameObjectHandler.Name);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                LogDownloadError(Logger, ex, transfer.Hash, gameObjectHandler.Name);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }).ConfigureAwait(false);

        if (!unavailable.IsEmpty)
        {
            var actionableUnavailable = unavailable.Values
                .Where(entry => entry.Reason != FileDownloadNegativeReason.Missing)
                .ToList();
            if (actionableUnavailable.Count > 0)
            {
                var reasons = string.Join(" ", actionableUnavailable.Select(entry => entry.Message).Distinct(StringComparer.Ordinal).Take(2));
                Mediator.Publish(new NotificationMessage("Some files are temporarily unavailable",
                    $"{actionableUnavailable.Count} file(s) could not be downloaded. {reasons}",
                    Configuration.Models.NotificationType.Warning, TimeSpan.FromSeconds(8)));
            }
        }

        LogDownloadEnd(Logger, gameObjectHandler.Name);
    }

    private async Task DownloadAndExtractAsync(DownloadFileTransfer transfer, string expectedExtension,
        DownloadStatusStore.DownloadGroupHandle groupHandle, string tempPath, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var downloadedBytes = await DownloadToFileAsync(transfer, groupHandle, tempPath, ct).ConfigureAwait(false);
                groupHandle.SetStatus(DownloadStatus.Decompressing);
                var input = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (input.ConfigureAwait(false))
                {
                    var extractedPath = await ExtractScfToCacheAsync(input, transfer.Hash, expectedExtension, ct).ConfigureAwait(false);
                    LogExtracted(Logger, transfer.Hash, downloadedBytes, extractedPath);
                    PersistFileToStorage(transfer.Hash, extractedPath, downloadedBytes);
                }

                _usageStatisticsService.RecordDownloadedBytes(downloadedBytes);
                _negativeCache.Clear(transfer.Hash);
                groupHandle.MarkFileTransferred();
                return;
            }
            catch (FileGrantRejectedException) when (attempt == 0)
            {
                var refreshed = (await FilesGetSizes([transfer.Hash], ct).ConfigureAwait(false)).SingleOrDefault();
                if (refreshed == null || !refreshed.FileExists || refreshed.IsForbidden || refreshed.Size <= 0)
                {
                    var reason = refreshed?.IsForbidden == true
                        ? FileDownloadNegativeReason.Rejected
                        : FileDownloadNegativeReason.Missing;
                    var message = reason == FileDownloadNegativeReason.Rejected
                        ? "The requested file is blocked from transfer."
                        : "The requested file is not available on the server. Snowcloak will check again later.";
                    throw new FileDownloadUnavailableException(_negativeCache.Record(transfer.Hash, reason,
                        reason == FileDownloadNegativeReason.Rejected ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(10),
                        message));
                }

                transfer.Refresh(refreshed);
            }
            catch (FileGrantRejectedException)
            {
                throw new FileDownloadUnavailableException(_negativeCache.Record(transfer.Hash,
                    FileDownloadNegativeReason.Rejected, TimeSpan.FromMinutes(1),
                    "The refreshed file grant was rejected. Snowcloak will request a new grant later."));
            }
        }
    }

    private async Task<long> DownloadToFileAsync(DownloadFileTransfer transfer, DownloadStatusStore.DownloadGroupHandle groupHandle,
        string tempPath, CancellationToken ct)
    {
        var response = await _transport.OpenAsync(new DownloadFileRequest(transfer.DownloadUri, transfer.Hash, transfer.Total),
            groupHandle.SetStatus, ct).ConfigureAwait(false);
        await using (response.ConfigureAwait(false))
        {
            var directory = Path.GetDirectoryName(tempPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (output.ConfigureAwait(false))
            {
                var limit = _orchestrator.DownloadLimitPerSlot();
                LogStartingDownload(Logger, limit, tempPath);
                var throttled = new ThrottledStream(response.Stream, limit);
                _activeDownloadStreams.TryAdd(throttled, 0);
                try
                {
                    return await CopyToFileAsync(throttled, output, groupHandle, ct).ConfigureAwait(false);
                }
                finally
                {
                    _activeDownloadStreams.TryRemove(throttled, out _);
                    await throttled.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<long> CopyToFileAsync(Stream source, Stream destination,
        DownloadStatusStore.DownloadGroupHandle groupHandle, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
        long total = 0;
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, DownloadBufferSize), ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                groupHandle.AddBytes(bytesRead);
                total += bytesRead;
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string> ExtractScfToCacheAsync(Stream scfStream, string expectedHash, string expectedExtension,
        CancellationToken ct)
    {
        var start = scfStream.Position;
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

        scfStream.Position = start;
        var tempPath = _fileDbManager.GetTemporaryCacheFilePath(expectedHash + "-" + Guid.NewGuid().ToString("N"), "tmp");
        var directory = Path.GetDirectoryName(tempPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
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

            File.Move(tempPath, finalPath, true);
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
        if (!_orchestrator.IsInitialized)
        {
            throw new InvalidOperationException("FileTransferManager is not initialised");
        }

        using var response = await _orchestrator.SendRequestAsync(HttpMethod.Post,
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
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            LogCacheEntryError(Logger, ex);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogTemporaryDeleteError(Logger, ex, path);
        }
    }

    private static string GetDownloadGroupKey(DownloadFileTransfer transfer) =>
        transfer.DownloadUri.Host + ":" + transfer.DownloadUri.Port;

    [LoggerMessage(Level = LogLevel.Trace, Message = "Starting download with a speed limit of {limit} to {tempPath}")]
    private static partial void LogStartingDownload(ILogger logger, long limit, string tempPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Extracted extension {ext} differs from expected {expectedExt} for {hash}")]
    private static partial void LogExtractedExtensionMismatch(ILogger logger, string ext, string expectedExt, string hash);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Extracted {file}:{length} => {dest}")]
    private static partial void LogExtracted(ILogger logger, string file, long length, string dest);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Setting new download speed limit to {Limit}")]
    private static partial void LogDownloadLimitChanged(ILogger logger, long limit);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Download start: {Id}")]
    private static partial void LogDownloadStart(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error downloading {Hash} for {Id}")]
    private static partial void LogDownloadError(ILogger logger, Exception exception, string hash, string id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Download end: {Id}")]
    private static partial void LogDownloadEnd(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error creating cache entry")]
    private static partial void LogCacheEntryError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not delete temporary download file {Path}")]
    private static partial void LogTemporaryDeleteError(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Error disposing active download stream")]
    private static partial void LogDisposeError(ILogger logger, Exception exception);
}
