using Dalamud.Utility;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.Files;
using Snowcloak.API.Routes;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.CacheFile;
using Snowcloak.Utils;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI.Files.Models;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

namespace Snowcloak.WebAPI.Files;

public partial class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private const int SmallDownloadBufferSize = 64 * 1024;
    private const int LargeDownloadBufferSize = 256 * 1024;
    private const long DownloadProgressReportByteInterval = 256 * 1024;
    private static readonly TimeSpan DownloadProgressReportMinInterval = TimeSpan.FromMilliseconds(100);
    private readonly ConcurrentDictionary<string, FileDownloadStatus> _downloadStatus;
    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly List<ThrottledStream> _activeDownloadStreams;

    public FileDownloadManager(ILogger<FileDownloadManager> logger, SnowMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileCacheManager) : base(logger, mediator)
    {
        _downloadStatus = new ConcurrentDictionary<string, FileDownloadStatus>(StringComparer.Ordinal);
        _orchestrator = orchestrator;
        _fileDbManager = fileCacheManager;
        _activeDownloadStreams = [];

        Mediator.Subscribe<DownloadLimitChangedMessage>(this, (msg) =>
        {
            if (!_activeDownloadStreams.Any()) return;
            var newLimit = _orchestrator.DownloadLimitPerSlot();
            Logger.LogTrace("Setting new Download Speed Limit to {newLimit}", newLimit);
            foreach (var stream in _activeDownloadStreams)
            {
                stream.BandwidthLimit = newLimit;
            }
        });
    }

    public List<DownloadFileTransfer> CurrentDownloads { get; private set; } = [];

    public List<FileTransfer> ForbiddenTransfers => _orchestrator.ForbiddenTransfers;

    public bool IsDownloading => !CurrentDownloads.Any();

    public void ClearDownload()
    {
        CurrentDownloads.Clear();
        _downloadStatus.Clear();
    }

    public async Task DownloadFiles(GameObjectHandler gameObject, List<FileReplacementData> fileReplacementDto, CancellationToken ct, string? uid = null)
    {
        Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles)));
        try
        {
            await DownloadFilesInternal(gameObject, fileReplacementDto, ct, uid).ConfigureAwait(false);
        }
        catch
        {
            ClearDownload();
        }
        finally
        {
            Mediator.Publish(new DownloadFinishedMessage(gameObject, uid));
            Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        ClearDownload();
        foreach (var stream in _activeDownloadStreams.ToList())
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // do nothing
                //
            }
        }
        base.Dispose(disposing);
    }

    private static byte ConvertReadByte(int byteOrEof)
    {
        if (byteOrEof == -1)
        {
            throw new EndOfStreamException();
        }

        return (byte)byteOrEof;
    }

    private static (string fileHash, long fileLengthBytes) ReadBlockFileHeader(FileStream fileBlockStream)
    {
        List<char> hashName = [];
        List<char> fileLength = [];
        var separator = (char)ConvertReadByte(fileBlockStream.ReadByte());
        if (separator != '#') throw new InvalidDataException("Data is invalid, first char is not #");

        bool readHash = false;
        while (true)
        {
            int readByte = fileBlockStream.ReadByte();
            if (readByte == -1)
                throw new EndOfStreamException();

            var readChar = (char)ConvertReadByte(readByte);
            if (readChar == ':')
            {
                readHash = true;
                continue;
            }
            if (readChar == '#') break;
            if (!readHash) hashName.Add(readChar);
            else fileLength.Add(readChar);
        }
        if (fileLength.Count == 0)
            fileLength.Add('0');
        return (string.Join("", hashName), long.Parse(string.Join("", fileLength)));
    }

    private async Task DownloadAndMungeFileHttpClient(string downloadGroup, Guid requestId, List<DownloadFileTransfer> fileTransfer, string tempPath, IProgress<long> progress, CancellationToken ct)
    {
        Logger.LogDebug("GUID {requestId} on server {uri} for files {files}", requestId, fileTransfer[0].DownloadUri, string.Join(", ", fileTransfer.Select(c => c.Hash).ToList()));

        await WaitForDownloadReady(fileTransfer, requestId, ct).ConfigureAwait(false);

        if (_downloadStatus.TryGetValue(downloadGroup, out var status))
        {
            status.DownloadStatus = DownloadStatus.Downloading;
        }
        var requestUrl = SnowFiles.CacheGetFullPath(fileTransfer[0].DownloadUri, requestId);

        Logger.LogDebug("Downloading {requestUrl} for request {id}", requestUrl, requestId);
        using var response = await SendDownloadRequestAsync(requestUrl, ct).ConfigureAwait(false);

        ThrottledStream? stream = null;
        try
        {
            var fileStream = File.Create(tempPath);
            await using (fileStream.ConfigureAwait(false))
            {
                var bufferSize = response.Content.Headers.ContentLength > 1024 * 1024
                    ? LargeDownloadBufferSize
                    : SmallDownloadBufferSize;
                var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

                var bytesRead = 0;
                long pendingProgressBytes = 0;
                long lastProgressReportTimestamp = Stopwatch.GetTimestamp();
                var limit = _orchestrator.DownloadLimitPerSlot();
                Logger.LogTrace("Starting Download of {id} with a speed limit of {limit} to {tempPath}", requestId, limit, tempPath);
                stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit);
                _activeDownloadStreams.Add(stream);
                try
                {
                    while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct).ConfigureAwait(false)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();

                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);

                        pendingProgressBytes += bytesRead;

                        long currentTimestamp = Stopwatch.GetTimestamp();
                        bool byteThresholdReached = pendingProgressBytes >= DownloadProgressReportByteInterval;
                        bool timeThresholdReached =
                            Stopwatch.GetElapsedTime(lastProgressReportTimestamp, currentTimestamp) >= DownloadProgressReportMinInterval;

                        if (!byteThresholdReached && !timeThresholdReached)
                        {
                            continue;
                        }

                        progress.Report(pendingProgressBytes);
                        pendingProgressBytes = 0;
                        lastProgressReportTimestamp = currentTimestamp;
                    }

                    if (pendingProgressBytes > 0)
                    {
                        progress.Report(pendingProgressBytes);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                Logger.LogDebug("{requestUrl} downloaded to {tempPath}", requestUrl, tempPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            try
            {
                if (!tempPath.IsNullOrEmpty())
                    File.Delete(tempPath);
            }
            catch
            {
                // ignore if file deletion fails
            }
            throw;
        }
        finally
        {
            if (stream != null)
            {
                _activeDownloadStreams.Remove(stream);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(Uri requestUrl, CancellationToken ct)
    {
        try
        {
            var response = await _orchestrator.SendRequestAsync(HttpMethod.Get, requestUrl, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Error during download of {requestUrl}, HttpStatusCode: {code}", requestUrl, ex.StatusCode);
            if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                throw new InvalidDataException($"Http error {ex.StatusCode} (cancelled: {ct.IsCancellationRequested}): {requestUrl}", ex);
            }

            throw;
        }
    }

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
            if (!_orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, dto.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new DownloadFileTransfer(dto));
            }
        }

        CurrentDownloads = downloadFileInfoFromService.Distinct().Select(d => new DownloadFileTransfer(d))
            .Where(d => d.CanBeTransferred).ToList();

        return CurrentDownloads;
    }

    private async Task DownloadFilesInternal(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct, string? uid)
    {
        var downloadGroups = CurrentDownloads.GroupBy(f => f.DownloadUri.Host + ":" + f.DownloadUri.Port, StringComparer.Ordinal);

        foreach (var downloadGroup in downloadGroups)
        {
            _downloadStatus[downloadGroup.Key] = new FileDownloadStatus()
            {
                DownloadStatus = DownloadStatus.Initializing,
                TotalBytes = downloadGroup.Sum(c => c.Total),
                TotalFiles = 1,
                TransferredBytes = 0,
                TransferredFiles = 0
            };
        }

        Mediator.Publish(new DownloadStartedMessage(gameObjectHandler, _downloadStatus, uid));

        await Parallel.ForEachAsync(downloadGroups, new ParallelOptions()
        {
            MaxDegreeOfParallelism = downloadGroups.Count(),
            CancellationToken = ct,
        },
        async (fileGroup, token) =>
        {
            // let server predownload files
            using var requestIdResponse = await _orchestrator.SendRequestAsync(HttpMethod.Post, SnowFiles.RequestEnqueueFullPath(fileGroup.First().DownloadUri),
                fileGroup.Select(c => c.Hash), token).ConfigureAwait(false);
            requestIdResponse.EnsureSuccessStatusCode();
            var requestIdContent = await requestIdResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            Logger.LogDebug("Sent request for {n} files on server {uri} with result {result}", fileGroup.Count(), fileGroup.First().DownloadUri,
                requestIdContent);

            Guid requestId = Guid.Parse(requestIdContent.Trim('"'));

            Logger.LogDebug("GUID {requestId} for {n} files on server {uri}", requestId, fileGroup.Count(), fileGroup.First().DownloadUri);

            var blockFile = _fileDbManager.GetCacheFilePath(requestId.ToString("N"), "blk");
            FileInfo fi = new(blockFile);
            try
            {
                if (_downloadStatus.TryGetValue(fileGroup.Key, out var statusWaitingForSlot))
                {
                    statusWaitingForSlot.DownloadStatus = DownloadStatus.WaitingForSlot;
                }
                await _orchestrator.WaitForDownloadSlotAsync(token).ConfigureAwait(false);
                if (_downloadStatus.TryGetValue(fileGroup.Key, out var statusWaitingForQueue))
                {
                    statusWaitingForQueue.DownloadStatus = DownloadStatus.WaitingForQueue;
                }
                Progress<long> progress = new((bytesDownloaded) =>
                {
                    try
                    {
                        if (!_downloadStatus.TryGetValue(fileGroup.Key, out FileDownloadStatus? value)) return;
                        value.TransferredBytes += bytesDownloaded;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Could not set download progress");
                    }
                });
                await DownloadAndMungeFileHttpClient(fileGroup.Key, requestId, [.. fileGroup], blockFile, progress, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("{dlName}: Detected cancellation of download, partially extracting files for {id}", fi.Name, gameObjectHandler);
            }
            catch (Exception ex)
            {
                _orchestrator.ReleaseDownloadSlot();
                File.Delete(blockFile);
                Logger.LogError(ex, "{dlName}: Error during download of {id}", fi.Name, requestId);
                ClearDownload();
                return;
            }

            FileStream? fileBlockStream = null;
            var threadCount = Math.Clamp((int)(Environment.ProcessorCount / 2.0f), 2, 8);
            var tasks = new List<Task>();
            var expectedExtensionByHash = fileReplacement
                .GroupBy(replacement => replacement.Hash, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().GamePaths[0].Split(".")[^1], StringComparer.OrdinalIgnoreCase);
            using var extractionConcurrency = new SemaphoreSlim(threadCount, threadCount);
            try
            {
                if (_downloadStatus.TryGetValue(fileGroup.Key, out var status))
                {
                    status.TransferredFiles = 1;
                    status.DownloadStatus = DownloadStatus.Decompressing;
                }
                fileBlockStream = File.OpenRead(blockFile);
                while (fileBlockStream.Position < fileBlockStream.Length)
                {
                    (string fileHash, long fileLengthBytes) = ReadBlockFileHeader(fileBlockStream);
                    var chunkPosition = fileBlockStream.Position;
                    fileBlockStream.Position += fileLengthBytes;

                    if (!expectedExtensionByHash.TryGetValue(fileHash, out var expectedExtension))
                    {
                        throw new InvalidDataException($"Missing expected extension metadata for {fileHash}.");
                    }

                    tasks.Add(ExtractBlockChunkAsync());

                    async Task ExtractBlockChunkAsync()
                    {
                        await extractionConcurrency.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try
                        {
                            await using var fileChunkStream = new FileStream(blockFile, new FileStreamOptions()
                            {
                                BufferSize = 80000,
                                Mode = FileMode.Open,
                                Access = FileAccess.Read
                            });
                            fileChunkStream.Position = chunkPosition;

                            using var innerFileStream = new LimitedStream(fileChunkStream, fileLengthBytes)
                            {
                                DisposeUnderlying = false
                            };

                            long startPos = fileChunkStream.Position;
                            var extractedPath = await ScfFile.ExtractSCFFile(innerFileStream, _fileDbManager.CacheFolder, ct).ConfigureAwait(false);                            long readBytes = fileChunkStream.Position - startPos;
                            if (readBytes != fileLengthBytes)
                            {
                                throw new EndOfStreamException();
                            }

                            var extractedHash = Path.GetFileNameWithoutExtension(extractedPath);
                            if (!string.Equals(extractedHash, fileHash, StringComparison.OrdinalIgnoreCase))
                            {
                                Logger.LogError("Hash mismatch after extracting SCF, got {hash}, expected {expectedHash}, deleting file", extractedHash, fileHash);
                                File.Delete(extractedPath);
                                return;
                            }

                            var extractedExtension = Path.GetExtension(extractedPath).TrimStart('.');
                            if (!string.Equals(extractedExtension, expectedExtension, StringComparison.OrdinalIgnoreCase))
                            {
                                Logger.LogDebug("{dlName}: Extracted extension {ext} differs from expected {expectedExt} for {hash}", fi.Name, extractedExtension, expectedExtension, fileHash);
                            }

                            Logger.LogDebug("{dlName}: Extracted {file}:{le} => {dest}", fi.Name, fileHash, fileLengthBytes, extractedPath);
                            PersistFileToStorage(fileHash, extractedPath, fileLengthBytes);
                        }
                        catch (EndOfStreamException)
                        {
                            Logger.LogWarning("{dlName}: Failure to extract file {fileHash}, stream ended prematurely", fi.Name, fileHash);
                        }
                        catch (Exception e)
                        {
                            Logger.LogWarning(e, "{dlName}: Error during decompression of {hash}", fi.Name, fileHash);

                            foreach (var fr in fileReplacement)
                                Logger.LogWarning(" - {h}: {x}", fr.Hash, fr.GamePaths[0]);
                        }
                        finally
                        {
                            extractionConcurrency.Release();
                        }
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Logger.LogDebug("{dlName}: Failure to extract file header data, stream ended", fi.Name);
            }
            catch (Exception ex)
            {
                Logger.LogInformation(ex, "{dlName}: Error during block file read. This is probably fine, and will fix itself.", fi.Name);
            }
            finally
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
                _orchestrator.ReleaseDownloadSlot();
                if (fileBlockStream != null)
                    await fileBlockStream.DisposeAsync().ConfigureAwait(false);
                File.Delete(blockFile);
            }
        }).ConfigureAwait(false);

        Logger.LogDebug("Download end: {id}", gameObjectHandler);

        ClearDownload();
    }

    private async Task<List<DownloadFileDto>> FilesGetSizes(List<string> hashes, CancellationToken ct)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");
        using var response = await _orchestrator.SendRequestAsync(HttpMethod.Get, SnowFiles.ServerFilesGetSizesFullPath(_orchestrator.FilesCdnUri!), hashes, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DownloadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private void PersistFileToStorage(string fileHash, string filePath, long? compressedSize = null)
    {
        try
        {
            var entry = _fileDbManager.CreateCacheEntry(filePath, fileHash);
            if (entry != null && !string.Equals(entry.Hash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                _fileDbManager.RemoveHashedFile(entry.Hash, entry.PrefixedFilePath);
                entry = null;
            }
            if (entry != null)
                entry.CompressedSize = compressedSize;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating cache entry");
        }
    }

    private async Task WaitForDownloadReady(List<DownloadFileTransfer> downloadFileTransfer, Guid requestId, CancellationToken downloadCt)
    {
        bool alreadyCancelled = false;
        try
        {
            CancellationTokenSource localTimeoutCts = new();
            localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            CancellationTokenSource composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);

            while (!_orchestrator.IsDownloadReady(requestId))
            {
                try
                {
                    await Task.Delay(250, composite.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    if (downloadCt.IsCancellationRequested) throw;

                    using var req = await _orchestrator.SendRequestAsync(HttpMethod.Get, SnowFiles.RequestCheckQueueFullPath(downloadFileTransfer[0].DownloadUri, requestId),
                        downloadFileTransfer.Select(c => c.Hash).ToList(), downloadCt).ConfigureAwait(false);
                    req.EnsureSuccessStatusCode();
                    localTimeoutCts.Dispose();
                    composite.Dispose();
                    localTimeoutCts = new();
                    localTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                    composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);
                }
            }

            localTimeoutCts.Dispose();
            composite.Dispose();

            Logger.LogDebug("Download {requestId} ready", requestId);
        }
        catch (TaskCanceledException)
        {
            try
            {
                using var _ = await _orchestrator.SendRequestAsync(HttpMethod.Get, SnowFiles.RequestCancelFullPath(downloadFileTransfer[0].DownloadUri, requestId)).ConfigureAwait(false);
                alreadyCancelled = true;
            }
            catch
            {
                // ignore whatever happens here
            }

            throw;
        }
        finally
        {
            if (downloadCt.IsCancellationRequested && !alreadyCancelled)
            {
                try
                {
                    using var _ = await _orchestrator.SendRequestAsync(HttpMethod.Get, SnowFiles.RequestCancelFullPath(downloadFileTransfer[0].DownloadUri, requestId)).ConfigureAwait(false);
                }
                catch
                {
                    // ignore whatever happens here
                }
            }
            _orchestrator.ClearDownloadRequest(requestId);
        }
    }
}
