using Snowcloak.API.Data;
using Snowcloak.API.Dto.Files;
using Snowcloak.API.Routes;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI.Files.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Snowcloak.WebAPI.Files;

public sealed class FileUploadManager : DisposableMediatorSubscriberBase
{
    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly ServerRegistry _serverManager;
    private readonly UsageStatisticsService _usageStatisticsService;
    private readonly SemaphoreSlim _characterDataUploadGate = new(1, 1);
    private readonly Lock _currentUploadsLock = new();
    private readonly List<FileTransfer> _currentUploads = [];
    private CancellationTokenSource? _uploadCancellationTokenSource;

    public FileUploadManager(ILogger<FileUploadManager> logger, SnowMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileDbManager,
        ServerRegistry serverManager,
        UsageStatisticsService usageStatisticsService) : base(logger, mediator)
    {
        _orchestrator = orchestrator;
        _fileDbManager = fileDbManager;
        _serverManager = serverManager;
        _usageStatisticsService = usageStatisticsService;

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            Reset();
        });
        Mediator.Subscribe<ConnectionLostMessage>(this, (msg) =>
        {
            Reset();
        });
    }

    public bool IsUploading
    {
        get
        {
            lock (_currentUploadsLock)
            {
                return _currentUploads.Count > 0;
            }
        }
    }

    public IReadOnlyList<UploadStatusSnapshot> GetCurrentUploadsSnapshot()
    {
        lock (_currentUploadsLock)
        {
            return _currentUploads.Select(t => new UploadStatusSnapshot(t.Hash, t.Transferred, t.Total)).ToList();
        }
    }

    public bool CancelUpload()
    {
        bool hasUploads;
        lock (_currentUploadsLock)
        {
            hasUploads = _currentUploads.Count > 0;
        }

        var uploadScope = Interlocked.Exchange(ref _uploadCancellationTokenSource, null);
        if (!hasUploads && uploadScope == null) return false;

        Logger.LogDebug("Cancelling current upload");
        uploadScope?.Cancel();
        uploadScope?.Dispose();
        ClearCurrentUploads();
        return true;
    }

    public async Task DeleteAllFiles()
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");

        await _orchestrator.SendRequestAsync(HttpMethod.Post, SnowFiles.ServerFilesDeleteAllFullPath(_orchestrator.FilesCdnUri!)).ConfigureAwait(false);
    }

    public async Task<List<string>> UploadFiles(List<string> hashesToUpload, IProgress<string> progress, CancellationToken? ct = null)
    {
        var token = ct ?? CancellationToken.None;
        Logger.LogDebug("Trying to upload files");
        var filesPresentLocally = hashesToUpload.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);
        var locallyMissingFiles = hashesToUpload.Except(filesPresentLocally, StringComparer.Ordinal).ToList();
        if (locallyMissingFiles.Count != 0)
        {
            return locallyMissingFiles;
        }

        progress.Report($"Starting upload for {filesPresentLocally.Count} files");

        var filesToUpload = await FilesSend([.. filesPresentLocally], [], token).ConfigureAwait(false);
        if (filesToUpload.Exists(f => f.IsForbidden))
        {
            return [.. filesToUpload.Where(f => f.IsForbidden).Select(f => f.Hash)];
        }

        var hashes = filesToUpload.Select(f => f.Hash).ToList();
        await RunUploadPipelineAsync(hashes, metadataProvider: null,
            beforeItem: (index, _) => progress.Report($"Uploading file {index + 1}/{hashes.Count}. Please wait until the upload is completed."),
            onCompressed: null, postProgress: false, trackedProvider: null, token).ConfigureAwait(false);

        return [];
    }

    public async Task<List<string>> UploadFilesWithMetadata(IReadOnlyDictionary<string, IReadOnlyDictionary<string, byte[]>> metadataByHash, CancellationToken? ct = null, IProgress<string>? progress = null)
    {
        var hashesToUpload = metadataByHash.Keys.ToList();
        if (hashesToUpload.Count == 0)
        {
            return [];
        }

        var token = ct ?? CancellationToken.None;
        Logger.LogDebug("Trying to upload files with metadata");
        var filesPresentLocally = hashesToUpload.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);
        var locallyMissingFiles = hashesToUpload.Except(filesPresentLocally, StringComparer.Ordinal).ToList();
        if (locallyMissingFiles.Count != 0)
        {
            return locallyMissingFiles;
        }

        var reporter = progress ?? new Progress<string>(_ => { });
        reporter.Report($"Starting upload for {filesPresentLocally.Count} files");

        var filesToUpload = await FilesSend([.. filesPresentLocally], [], token).ConfigureAwait(false);
        if (filesToUpload.Exists(f => f.IsForbidden))
        {
            return [.. filesToUpload.Where(f => f.IsForbidden).Select(f => f.Hash)];
        }

        var hashes = filesToUpload.Select(f => f.Hash).ToList();
        await RunUploadPipelineAsync(hashes,
            metadataProvider: hash => metadataByHash.TryGetValue(hash, out var metadata) ? metadata : null,
            beforeItem: (index, _) => reporter.Report($"Uploading file {index + 1}/{hashes.Count}. Please wait until the upload is completed."),
            onCompressed: null, postProgress: false, trackedProvider: null, token).ConfigureAwait(false);

        return [];
    }

    public async Task<CharacterData> UploadFiles(CharacterData data, List<UserData> visiblePlayers, CancellationToken ct = default)
    {
        await _characterDataUploadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CancelUpload();

            using var uploadScope = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _uploadCancellationTokenSource = uploadScope;
            var uploadToken = uploadScope.Token;
            Logger.LogDebug("Sending Character data {hash} to service {url}", data.DataHash.Value, _serverManager.CurrentRealApiUrl);

            var uploadableHashCount = data.FileReplacements.Values
                .SelectMany(replacements => replacements)
                .Where(replacement => string.IsNullOrEmpty(replacement.FileSwapPath) && !string.IsNullOrEmpty(replacement.Hash))
                .Select(replacement => replacement.Hash)
                .Distinct(StringComparer.Ordinal)
                .Count();
            HashSet<string> hashesToVerify = GetHashesToVerify(data);
            Logger.LogInformation(
                "Preparing upload verification for {hash}: uploadableHashes={uploadableHashCount}, hashesToVerify={hashesToVerifyCount}, visibleUsers={visibleUserCount}",
                data.DataHash.Value,
                uploadableHashCount,
                hashesToVerify.Count,
                visiblePlayers.Count);
            if (hashesToVerify.Count > 0)
            {
                // FilesSend is the authority for the current file-service storage. A client-local
                // success cache can outlive a replaced/cleared file volume while SignalR remains
                // connected, which publishes manifests containing hashes the receiver cannot fetch.
                await VerifyAndUploadFiles(data.DataHash.Value, hashesToVerify, visiblePlayers, uploadToken).ConfigureAwait(false);
                Logger.LogInformation("Upload complete for {hash}", data.DataHash.Value);
            }
            else
            {
                Logger.LogInformation("No upload hashes exist for {hash}; skipping FilesSend", data.DataHash.Value);
            }

            // FilesSend remains authoritative for refusing forbidden uploads, but the replacement
            // metadata must stay in the manifest so receivers can use an existing local copy.
            return data;
        }
        finally
        {
            _uploadCancellationTokenSource = null;
            ClearCurrentUploads();
            _characterDataUploadGate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Reset();
    }

    private async Task RunUploadPipelineAsync(
        IReadOnlyList<string> hashes,
        Func<string, IReadOnlyDictionary<string, byte[]>?>? metadataProvider,
        Action<int, string>? beforeItem,
        Action<string, long>? onCompressed,
        bool postProgress,
        Func<string, IReadOnlyList<FileTransfer>?>? trackedProvider,
        CancellationToken ct)
    {
        Task uploadTask = Task.CompletedTask;
        for (var index = 0; index < hashes.Count; index++)
        {
            var hash = hashes[index];
            beforeItem?.Invoke(index, hash);
            Logger.LogDebug("[{hash}] Compressing", hash);

            var data = await _fileDbManager.GetCompressedFileData(hash, ct, metadataProvider?.Invoke(hash)).ConfigureAwait(false);
            MemoryStream? pendingStream = data.Item2;
            try
            {
                onCompressed?.Invoke(hash, pendingStream.Length);
                Logger.LogDebug("[{hash}] Starting upload for {filePath}", data.Item1, _fileDbManager.GetFileCacheByHash(data.Item1)?.ResolvedFilepath);
                await uploadTask.ConfigureAwait(false);
                uploadTask = UploadFileAsync(pendingStream, hash, postProgress, ct, trackedProvider?.Invoke(hash));
                pendingStream = null;
                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                if (pendingStream != null)
                {
                    await pendingStream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        await uploadTask.ConfigureAwait(false);
    }

    private async Task<List<UploadFileDto>> FilesSend(List<string> hashes, List<string> uids, CancellationToken ct)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");
        FilesSendDto filesSendDto = new()
        {
            FileHashes = hashes,
            UIDs = uids
        };
        using var response = await _orchestrator.SendRequestAsync(HttpMethod.Post, SnowFiles.ServerFilesFilesSendFullPath(_orchestrator.FilesCdnUri!), filesSendDto, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Logger.LogWarning("FilesSend failed with {status}: {body}", response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }
        return await response.Content.ReadFromJsonAsync<List<UploadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private static HashSet<string> GetHashesToVerify(CharacterData data)
    {
        HashSet<string> hashesToVerify = new(StringComparer.Ordinal);
        foreach (var replacements in data.FileReplacements.Values)
        {
            foreach (var replacement in replacements)
            {
                if (string.IsNullOrEmpty(replacement.FileSwapPath) && !string.IsNullOrEmpty(replacement.Hash))
                {
                    hashesToVerify.Add(replacement.Hash);
                }
            }
        }
        return hashesToVerify;
    }

    private void Reset()
    {
        var uploadScope = Interlocked.Exchange(ref _uploadCancellationTokenSource, null);
        uploadScope?.Cancel();
        uploadScope?.Dispose();
        ClearCurrentUploads();
    }

    private async Task UploadFileAsync(Stream compressedFile, string fileHash, bool postProgress, CancellationToken uploadToken,
        IReadOnlyList<FileTransfer>? trackedUploads)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("FileTransferManager is not initialized");

        try
        {
            // Capture the length up front: UploadFileStream wraps the stream in ProgressableStreamContent,
            // which disposes the underlying stream once sent, so it can't be read afterwards.
            var compressedLength = compressedFile.Length;
            Logger.LogInformation("[{hash}] Uploading {size}", fileHash, ElezenImgui.ByteToString(compressedLength));

            if (uploadToken.IsCancellationRequested) return;

            await UploadFileStream(compressedFile, fileHash, postProgress, uploadToken, trackedUploads).ConfigureAwait(false);
            _usageStatisticsService.RecordUploadedBytes(compressedLength);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("[{hash}] Upload cancelled", fileHash);
            throw;
        }
        finally
        {
            await compressedFile.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task UploadFileStream(Stream compressedFile, string fileHash, bool postProgress, CancellationToken uploadToken,
        IReadOnlyList<FileTransfer>? trackedUploads)
    {
        if (compressedFile.CanSeek)
        {
            compressedFile.Position = 0;
        }

        Progress<UploadProgress>? progressTracker = !postProgress ? null : new((update) =>
        {
            try
            {
                if (trackedUploads == null || trackedUploads.Count == 0)
                {
                    return;
                }

                if (trackedUploads.Count > 1)
                {
                    Logger.LogDebug("[{hash}] Multiple upload entries tracked during progress update", fileHash);
                }

                foreach (var upload in trackedUploads)
                {
                    upload.Total = update.Size;
                    upload.Transferred = update.Uploaded;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{hash}] Could not set upload progress", fileHash);
            }
        });

        using var streamContent = new ProgressableStreamContent(compressedFile, progressTracker);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _orchestrator.SendRequestStreamAsync(HttpMethod.Post, SnowFiles.ServerFilesUploadFullPath(_orchestrator.FilesCdnUri!, fileHash), streamContent, uploadToken).ConfigureAwait(false);
        Logger.LogDebug("[{hash}] Upload Status: {status}", fileHash, response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    private async Task VerifyAndUploadFiles(string dataHash, HashSet<string> hashesToVerify, List<UserData> visiblePlayers, CancellationToken uploadToken)
    {
        Dictionary<string, FileCacheEntity> cachedEntriesByHash = new(StringComparer.Ordinal);
        foreach (var hash in hashesToVerify)
        {
            var cacheEntry = _fileDbManager.GetFileCacheByHash(hash);
            if (cacheEntry != null)
            {
                cachedEntriesByHash[hash] = cacheEntry;
            }
        }

        var locallyMissingCount = hashesToVerify.Count - cachedEntriesByHash.Count;
        if (locallyMissingCount > 0)
        {
            throw new InvalidOperationException(
                $"Cannot publish the appearance because {locallyMissingCount} referenced file(s) are not available locally.");
        }

        hashesToVerify = cachedEntriesByHash.Keys.ToHashSet(StringComparer.Ordinal);
        if (hashesToVerify.Count == 0)
        {
            return;
        }

        Logger.LogDebug("Verifying {count} files", hashesToVerify.Count);
        var filesToUpload = await FilesSend([.. hashesToVerify], visiblePlayers.Select(p => p.UID).ToList(), uploadToken).ConfigureAwait(false);
        var forbiddenCount = filesToUpload.Count(file => file.IsForbidden);
        var missingCount = filesToUpload.Count - forbiddenCount;
        Logger.LogInformation(
            "FilesSend result for {hash}: verifiedHashes={verifiedHashCount}, serverMissingHashes={missingHashCount}, forbiddenHashes={forbiddenHashCount}, visibleUsers={visibleUserCount}",
            dataHash,
            hashesToVerify.Count,
            missingCount,
            forbiddenCount,
            visiblePlayers.Count);

        HashSet<string> handledUploads = new(StringComparer.Ordinal);
        foreach (var file in filesToUpload)
        {
            if (file.IsForbidden)
            {
                _orchestrator.AddForbiddenTransfer(new ForbiddenTransfer(file.Hash, file.ForbiddenBy, ForbiddenTransferKind.Upload,
                    cachedEntriesByHash.TryGetValue(file.Hash, out var forbiddenEntry) ? forbiddenEntry.ResolvedFilepath : string.Empty));
                continue;
            }

            if (!handledUploads.Add(file.Hash) || HasCurrentUpload(file.Hash))
            {
                continue;
            }

            try
            {
                if (!cachedEntriesByHash.TryGetValue(file.Hash, out var cacheEntry))
                {
                    Logger.LogWarning("Tried to request file {hash} but file was not present", file.Hash);
                    continue;
                }

                var resolvedPath = cacheEntry.ResolvedFilepath;
                var originalSize = cacheEntry.Size ?? new FileInfo(resolvedPath).Length;

                AddCurrentUpload(new UploadFileTransfer(file)
                {
                    LocalFile = resolvedPath,
                    Total = originalSize,
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Tried to request file {hash} but file was not present", file.Hash);
            }
        }

        var currentUploads = GetCurrentUploadsLive();
        var totalSize = currentUploads.Sum(c => c.Total);
        var trackedUploadsByHash = currentUploads
            .GroupBy(transfer => transfer.Hash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<FileTransfer>)[.. group], StringComparer.Ordinal);
        Logger.LogDebug("Compressing and uploading files");

        var pendingHashes = currentUploads.Where(f => f.CanBeTransferred && !f.IsTransferred).Select(f => f.Hash).ToList();
        await RunUploadPipelineAsync(pendingHashes, metadataProvider: null, beforeItem: null,
            onCompressed: (hash, compressedLength) =>
            {
                if (!trackedUploadsByHash.TryGetValue(hash, out var tracked))
                {
                    Logger.LogWarning("[{hash}] Missing upload tracking entry while setting compressed size", hash);
                    return;
                }

                foreach (var upload in tracked)
                {
                    upload.Total = compressedLength;
                    upload.Transferred = 0;
                }
            },
            postProgress: true,
            trackedProvider: hash => trackedUploadsByHash.TryGetValue(hash, out var tracked) ? tracked : null,
            uploadToken).ConfigureAwait(false);

        if (HasCurrentUploads())
        {
            var compressedSize = currentUploads.Sum(c => c.Total);
            Logger.LogDebug("Upload complete, compressed {size} to {compressed}", ElezenImgui.ByteToString(totalSize), ElezenImgui.ByteToString(compressedSize));

            _fileDbManager.WriteOutFullIndex();
        }

        ClearCurrentUploads();
    }

    private void AddCurrentUpload(FileTransfer upload)
    {
        lock (_currentUploadsLock)
        {
            _currentUploads.Add(upload);
        }
    }

    private List<FileTransfer> GetCurrentUploadsLive()
    {
        lock (_currentUploadsLock)
        {
            return [.. _currentUploads];
        }
    }

    private bool HasCurrentUpload(string hash)
    {
        lock (_currentUploadsLock)
        {
            return _currentUploads.Any(f => string.Equals(f.Hash, hash, StringComparison.Ordinal));
        }
    }

    private bool HasCurrentUploads()
    {
        lock (_currentUploadsLock)
        {
            return _currentUploads.Count > 0;
        }
    }

    private void ClearCurrentUploads()
    {
        lock (_currentUploadsLock)
        {
            _currentUploads.Clear();
        }
    }
}
