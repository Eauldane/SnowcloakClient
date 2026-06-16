using Microsoft.Extensions.Logging;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;

namespace Snowcloak.FileCache;

public sealed class CacheMonitor : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly SnowcloakConfigService _configService;
    private readonly FileCacheManager _fileDbManager;
    private readonly IpcManager _ipcManager;
    private readonly CacheScanPauseGate _pauseGate = new();
    private readonly CacheScanner _scanner;
    private readonly CacheEvictionService _eviction;
    private readonly Lock _changeHandlingLock = new();
    private int _disposed;

    public CacheMonitor(ILogger<CacheMonitor> logger, IpcManager ipcManager, SnowcloakConfigService configService,
        FileCacheManager fileDbManager, SnowMediator mediator, PerformanceCollectorService performanceCollector,
        FileCompactor fileCompactor, DatabaseService databaseService) : base(logger, mediator)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(ipcManager);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(fileDbManager);
        ArgumentNullException.ThrowIfNull(performanceCollector);
        ArgumentNullException.ThrowIfNull(fileCompactor);
        ArgumentNullException.ThrowIfNull(databaseService);

        _backgroundTasks = new BackgroundTaskTracker(logger);
        _ipcManager = ipcManager;
        _configService = configService;
        _fileDbManager = fileDbManager;
        _eviction = new CacheEvictionService(logger, configService, fileDbManager, fileCompactor, databaseService);
        _scanner = new CacheScanner(logger, fileDbManager, ipcManager, configService, performanceCollector,
            _backgroundTasks, OnInitialScanComplete);

        Mediator.Subscribe<PenumbraInitializedMessage>(this, (_) =>
        {
            StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
            StartSnowWatcher(configService.Current.CacheFolder);
            StartSubstWatcher(_fileDbManager.SubstFolder);
            InvokeScan();
        });
        Mediator.Subscribe<HaltScanMessage>(this, (msg) => _pauseGate.Hold(msg.Source));
        Mediator.Subscribe<ResumeScanMessage>(this, (msg) => _pauseGate.Release(msg.Source));
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) =>
        {
            StartSnowWatcher(configService.Current.CacheFolder);
            StartSubstWatcher(_fileDbManager.SubstFolder);
            StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
            InvokeScan();
        });
        Mediator.Subscribe<PenumbraDirectoryChangedMessage>(this, (msg) =>
        {
            StartPenumbraWatcher(msg.ModDirectory);
            InvokeScan();
        });

        if (_ipcManager.Penumbra.APIAvailable && !string.IsNullOrEmpty(_ipcManager.Penumbra.ModDirectory))
        {
            StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
        }
        if (configService.Current.HasValidSetup())
        {
            StartSnowWatcher(configService.Current.CacheFolder);
            StartSubstWatcher(_fileDbManager.SubstFolder);
            InvokeScan();
        }
    }

    public CacheFolderWatcher? PenumbraWatcher { get; private set; }
    public CacheFolderWatcher? SnowWatcher { get; private set; }
    public CacheFolderWatcher? SubstWatcher { get; private set; }

    public long FileCacheSize => _eviction.FileCacheSize;
    public long FileCacheDriveFree => _eviction.FileCacheDriveFree;
    public bool StorageisNTFS => _eviction.StorageisNTFS;

    public long TotalFiles => _scanner.TotalFiles;
    public long TotalFilesStorage => _scanner.TotalFilesStorage;
    public long CurrentFileProgress => _scanner.CurrentFileProgress;
    public bool IsScanRunning => _scanner.IsScanRunning;

    public bool IsScanHalted => _pauseGate.IsPaused;
    public string DescribeHaltSources() => _pauseGate.Describe();
    public void ResetLocks() => _pauseGate.Reset();

    public void InvokeScan() => _scanner.InvokeScan();
    public void RecalculateFileCacheSize(CancellationToken token) => _eviction.RecalculateFileCacheSize(token);

    public void StartPenumbraWatcher(string? penumbraPath)
    {
        PenumbraWatcher?.Dispose();
        PenumbraWatcher = null;
        if (string.IsNullOrEmpty(penumbraPath))
        {
            Logger.LogWarning("Penumbra is not connected or the path is not set, cannot start FSW for Penumbra.");
            return;
        }

        Logger.LogDebug("Initializing Penumbra FSW on {path}", penumbraPath);
        PenumbraWatcher = new CacheFolderWatcher(penumbraPath, includeSubdirectories: true, watchModifications: true,
            TimeSpan.FromSeconds(10), _pauseGate, HandleChanges, _backgroundTasks, Logger, nameof(PenumbraWatcher));
    }

    public void StartSnowWatcher(string? snowPath)
    {
        SnowWatcher?.Dispose();
        SnowWatcher = null;
        if (string.IsNullOrEmpty(snowPath) || !Directory.Exists(snowPath))
        {
            Logger.LogWarning("Snowcloak file path is not set, cannot start the FSW for Snowcloak.");
            return;
        }

        _eviction.DetectStorageFormat();

        Logger.LogDebug("Initializing Snow FSW on {path}", snowPath);
        SnowWatcher = new CacheFolderWatcher(snowPath, includeSubdirectories: true, watchModifications: false,
            TimeSpan.FromSeconds(5), _pauseGate, HandleChanges, _backgroundTasks, Logger, nameof(SnowWatcher));
    }

    public void StartSubstWatcher(string? substPath)
    {
        SubstWatcher?.Dispose();
        SubstWatcher = null;
        if (string.IsNullOrEmpty(substPath))
        {
            Logger.LogWarning("Snowcloak file path is not set, cannot start the FSW for Snowcloak.");
            return;
        }

        try
        {
            if (!Directory.Exists(substPath))
                Directory.CreateDirectory(substPath);
        }
        catch
        {
            Logger.LogWarning("Could not create subst directory at {path}.", substPath);
            return;
        }

        Logger.LogDebug("Initializing Subst FSW on {path}", substPath);
        SubstWatcher = new CacheFolderWatcher(substPath, includeSubdirectories: true, watchModifications: false,
            TimeSpan.FromSeconds(5), _pauseGate, HandleChanges, _backgroundTasks, Logger, nameof(SubstWatcher));
    }

    public void StopMonitoring()
    {
        Logger.LogInformation("Stopping monitoring of Penumbra and Snowcloak storage folders");
        SnowWatcher?.Dispose();
        SubstWatcher?.Dispose();
        PenumbraWatcher?.Dispose();
        SnowWatcher = null;
        SubstWatcher = null;
        PenumbraWatcher = null;
    }

    public void ClearSubstStorage()
    {
        var substDir = _fileDbManager.SubstFolder;
        var allSubstFiles = Directory.GetFiles(substDir, "*.*", SearchOption.AllDirectories)
            .Where(IsSubstArtifact);

        SubstWatcher?.SuspendEvents();

        Dictionary<string, WatcherChange> changes = SubstWatcher?.SnapshotChanges()
            .ToDictionary(t => t.Key, t => new WatcherChange(WatcherChangeTypes.Deleted, t.Key), StringComparer.Ordinal)
            ?? new(StringComparer.Ordinal);

        foreach (var file in allSubstFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // ignore
            }
        }

        HandleChanges(changes);

        SubstWatcher?.ResumeEvents();
    }

    public void DeleteSubstOriginals()
    {
        var substDir = _fileDbManager.SubstFolder;
        var allSubstFiles = Directory.GetFiles(substDir, "*.*", SearchOption.AllDirectories)
            .Where(IsSubstArtifact);

        foreach (var substFile in allSubstFiles)
        {
            var hash = Path.GetFileNameWithoutExtension(substFile);
            var extension = Path.GetExtension(substFile).TrimStart('.');
            if (hash.Length != 64 || string.IsNullOrEmpty(extension))
            {
                continue;
            }

            var cacheFile = _fileDbManager.GetCacheFilePath(hash, extension);
            try
            {
                if (File.Exists(cacheFile))
                    File.Delete(cacheFile);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool IsSubstArtifact(string path)
    {
        var fileName = Path.GetFileName(path);
        var name = Path.GetFileNameWithoutExtension(fileName);
        return fileName.Length == 64
            || name.Length == 64
            || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private void HandleChanges(Dictionary<string, WatcherChange> changes)
    {
        lock (_changeHandlingLock)
        {
            var deletedEntries = changes.Where(c => c.Value.ChangeType == WatcherChangeTypes.Deleted).Select(c => c.Key);
            var renamedEntries = changes.Where(c => c.Value.ChangeType == WatcherChangeTypes.Renamed);
            var remainingEntries = changes.Where(c => c.Value.ChangeType != WatcherChangeTypes.Deleted).Select(c => c.Key);

            foreach (var entry in deletedEntries)
            {
                Logger.LogDebug("FSW Change: Deletion - {val}", entry);
            }

            foreach (var entry in renamedEntries)
            {
                Logger.LogDebug("FSW Change: Renamed - {oldVal} => {val}", entry.Value.OldPath, entry.Key);
            }

            foreach (var entry in remainingEntries)
            {
                Logger.LogDebug("FSW Change: Creation or Change - {val}", entry);
            }

            var allChanges = deletedEntries
                .Concat(renamedEntries.Select(c => c.Value.OldPath!))
                .Concat(renamedEntries.Select(c => c.Key))
                .Concat(remainingEntries)
                .ToArray();

            _ = _fileDbManager.GetFileCachesByPaths(allChanges);
        }
    }

    private void OnInitialScanComplete()
    {
        _configService.Update(c => c.InitialScanComplete = true);
        StartSnowWatcher(_configService.Current.CacheFolder);
        StartSubstWatcher(_fileDbManager.SubstFolder);
        StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing);
        _backgroundTasks.StopAccepting();
        StopMonitoring();
        _scanner.Dispose();
        _eviction.Dispose();
        _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(2), nameof(CacheMonitor));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing: true);
        _backgroundTasks.StopAccepting();
        StopMonitoring();
        _scanner.Dispose();
        _eviction.Dispose();
        await _backgroundTasks.StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
