using ElezenTools.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services;
using Snowcloak.Utils;
using System.Collections.Concurrent;

namespace Snowcloak.FileCache;

internal sealed partial class CacheScanner : IDisposable
{
    private readonly FileCacheManager _fileDbManager;
    private readonly IpcManager _ipcManager;
    private readonly SnowcloakConfigService _configService;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly Action _onInitialScanComplete;
    private readonly ILogger _logger;

    private long _currentFileProgress;
    private readonly Lock _scanLock = new();
    private CancellationTokenSource _scanCts = new();

    public CacheScanner(ILogger logger, FileCacheManager fileDbManager, IpcManager ipcManager,
        SnowcloakConfigService configService,
        PerformanceCollectorService performanceCollector, BackgroundTaskTracker backgroundTasks,
        Action onInitialScanComplete)
    {
        _logger = logger;
        _fileDbManager = fileDbManager;
        _ipcManager = ipcManager;
        _configService = configService;
        _performanceCollector = performanceCollector;
        _backgroundTasks = backgroundTasks;
        _onInitialScanComplete = onInitialScanComplete;
    }

    public long TotalFiles { get; private set; }
    public long TotalFilesStorage { get; private set; }
    public long CurrentFileProgress => Interlocked.Read(ref _currentFileProgress);
    public bool IsScanRunning => CurrentFileProgress > 0 || TotalFiles > 0;

    public void InvokeScan()
    {
        ResetProgress();

        CancellationToken token;
        lock (_scanLock)
        {
            _scanCts.Cancel();
            _scanCts.Dispose();
            _scanCts = new CancellationTokenSource();
            token = _scanCts.Token;
        }

        _ = _backgroundTasks.Run(async () =>
        {
            try
            {
                _logger.LogDebug("Starting Full File Scan");
                ResetProgress();

                while (Service.IsOnFramework)
                {
                    _logger.LogWarning("Scanner is on framework, waiting for leaving thread before continuing");
                    await Task.Delay(250, token).ConfigureAwait(false);
                }

                await _performanceCollector.LogPerformance(this, $"FullFileScan", () => FullFileScanAsync(token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _logger.LogDebug("Full File Scan cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Full File Scan");
            }
            finally
            {
                ResetProgress();
            }
        }, nameof(InvokeScan));
    }

    private void ResetProgress()
    {
        TotalFiles = 0;
        Interlocked.Exchange(ref _currentFileProgress, 0);
    }

    private async Task FullFileScanAsync(CancellationToken ct)
    {
        TotalFiles = 1;
        var penumbraDir = _ipcManager.Penumbra.ModDirectory;
        var substDir = _fileDbManager.SubstFolder;
        var cacheDir = _configService.Current.CacheFolder;

        var penDirExists = !string.IsNullOrEmpty(penumbraDir) && Directory.Exists(penumbraDir);
        var cacheDirExists = !string.IsNullOrEmpty(cacheDir) && Directory.Exists(cacheDir);
        if (!penDirExists) _logger.LogWarning("Penumbra directory is not set or does not exist.");
        if (!cacheDirExists) _logger.LogWarning("Snowcloak Cache directory is not set or does not exist.");
        if (!penDirExists || !cacheDirExists) return;

        try
        {
            if (!Directory.Exists(substDir))
                Directory.CreateDirectory(substDir);
        }
        catch
        {
            _logger.LogWarning("Could not create subst directory at {path}.", substDir);
        }

        _logger.LogDebug("Getting files from {penumbra} and {storage}", penumbraDir, cacheDir);

        var penumbraFiles = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(penumbraDir!))
        {
            try
            {
                penumbraFiles.AddRange(Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => SupportedFileTypes.IsAllowedPath(f)
                        && !f.Contains(@"\bg\", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains(@"\bgcommon\", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains(@"\ui\", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not enumerate path {path}", folder);
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;
        }

        var cacheFiles = EnumerateStorageFiles(cacheDir, substDir)
            .Where(IsContentAddressedStorageFile);

        if (ct.IsCancellationRequested) return;

        var allScannedFiles = penumbraFiles
            .Concat(cacheFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(t => t.ToUpperInvariant(), _ => false, StringComparer.OrdinalIgnoreCase);

        TotalFiles = allScannedFiles.Count;

        if (ct.IsCancellationRequested) return;

        var degreeOfParallelism = Math.Clamp((int)(Environment.ProcessorCount / 2.0f), 2, 8);
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism, CancellationToken = ct };

        var foundFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var entitiesToUpdate = new ConcurrentBag<FileCacheEntity>();
        var entitiesToRemove = new ConcurrentBag<FileCacheEntity>();

        var existingEntities = _fileDbManager.GetAllFileCaches();
        TotalFilesStorage = existingEntities.Count;

        await Parallel.ForEachAsync(existingEntities, parallelOptions, (workload, _) =>
        {
            if (!_ipcManager.Penumbra.APIAvailable)
            {
                _logger.LogWarning("Penumbra not available");
                return ValueTask.CompletedTask;
            }

            try
            {
                var validatedCacheResult = _fileDbManager.ValidateFileCacheEntity(workload);
                if (validatedCacheResult.State != FileState.RequireDeletion)
                {
                    foundFiles[validatedCacheResult.FileCache.ResolvedFilepath] = 0;
                }

                if (validatedCacheResult.State == FileState.RequireUpdate)
                {
                    LogToUpdate(_logger, validatedCacheResult.FileCache.ResolvedFilepath);
                    entitiesToUpdate.Add(validatedCacheResult.FileCache);
                }
                else if (validatedCacheResult.State == FileState.RequireDeletion)
                {
                    LogToDelete(_logger, validatedCacheResult.FileCache.ResolvedFilepath);
                    entitiesToRemove.Add(validatedCacheResult.FileCache);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed validating {path}", workload.ResolvedFilepath);
            }

            Interlocked.Increment(ref _currentFileProgress);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;
        if (!_ipcManager.Penumbra.APIAvailable)
        {
            _logger.LogWarning("Penumbra not available");
            return;
        }

        foreach (var found in foundFiles.Keys)
        {
            allScannedFiles[found] = true;
        }

        if (!entitiesToUpdate.IsEmpty || !entitiesToRemove.IsEmpty)
        {
            foreach (var entity in entitiesToUpdate)
            {
                await _fileDbManager.UpdateHashedFileAsync(entity).ConfigureAwait(false);
            }

            foreach (var entity in entitiesToRemove)
            {
                _fileDbManager.RemoveHashedFile(entity.Hash, entity.PrefixedFilePath);
            }
        }

        _logger.LogTrace("Scanner validated existing db files");

        if (!_ipcManager.Penumbra.APIAvailable)
        {
            _logger.LogWarning("Penumbra not available");
            return;
        }

        if (ct.IsCancellationRequested) return;

        var newFiles = allScannedFiles.Where(c => !c.Value).Select(c => c.Key).ToList();
        if (newFiles.Count > 0)
        {
            await Parallel.ForEachAsync(newFiles, parallelOptions, async (cachePath, _) =>
            {
                if (!_ipcManager.Penumbra.APIAvailable)
                {
                    _logger.LogWarning("Penumbra not available");
                    return;
                }

                try
                {
                    var isSubst = !string.IsNullOrEmpty(substDir) && cachePath.StartsWith(substDir, StringComparison.OrdinalIgnoreCase);
                    var hash = isSubst ? null : await Crypto.GetFileHashAsync(cachePath).ConfigureAwait(false);

                    var entry = _fileDbManager.CreateFileEntry(cachePath, hash);
                    if (entry == null)
                    {
                        if (isSubst)
                            _fileDbManager.CreateSubstEntry(cachePath);
                        else
                            _fileDbManager.CreateCacheEntry(cachePath, hash);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed adding {file}", cachePath);
                }

                Interlocked.Increment(ref _currentFileProgress);
            }).ConfigureAwait(false);

            _logger.LogTrace("Scanner added {notScanned} new files to db", newFiles.Count);
        }

        _logger.LogDebug("Scan complete");
        ResetProgress();

        if (!_configService.Current.InitialScanComplete)
        {
            _onInitialScanComplete();
        }
    }

    public void Dispose()
    {
        lock (_scanLock)
        {
            _scanCts.Cancel();
            _scanCts.Dispose();
        }
    }

    private static IEnumerable<string> EnumerateStorageFiles(string cacheDir, string substDir)
    {
        foreach (var file in Directory.EnumerateFiles(cacheDir, "*.*", SearchOption.AllDirectories))
        {
            if (!IsPathInsideDirectory(file, substDir))
            {
                yield return file;
            }
        }

        if (!string.IsNullOrEmpty(substDir) && Directory.Exists(substDir))
        {
            foreach (var file in Directory.EnumerateFiles(substDir, "*.*", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private static bool IsContentAddressedStorageFile(string path)
    {
        var fileName = Path.GetFileName(path);
        var name = Path.GetFileNameWithoutExtension(fileName);
        return fileName.Length == 64 && fileName.All(Uri.IsHexDigit)
            || name.Length == 64 && name.All(Uri.IsHexDigit);
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "To update: {path}")]
    private static partial void LogToUpdate(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Trace, Message = "To delete: {path}")]
    private static partial void LogToDelete(ILogger logger, string path);
}
