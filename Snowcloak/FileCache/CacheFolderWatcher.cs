using Microsoft.Extensions.Logging;
using Snowcloak.Utils;

namespace Snowcloak.FileCache;

public sealed record WatcherChange(WatcherChangeTypes ChangeType, string? OldPath = null);

public sealed class CacheFolderWatcher : IDisposable
{
    private const int InternalBufferSize = 8 * 1024 * 1024;

    private readonly FileSystemWatcher _watcher;
    private readonly Dictionary<string, WatcherChange> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _debounce;
    private readonly CacheScanPauseGate _pauseGate;
    private readonly Action<Dictionary<string, WatcherChange>> _onChanges;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly ILogger _logger;
    private readonly string _operationName;
    private readonly SingleFlightCts _drainFlight = new();

    public string Path { get; }

    internal CacheFolderWatcher(string path, bool includeSubdirectories, bool watchModifications, TimeSpan debounce,
        CacheScanPauseGate pauseGate, Action<Dictionary<string, WatcherChange>> onChanges,
        BackgroundTaskTracker backgroundTasks, ILogger logger, string operationName)
    {
        Path = path;
        _debounce = debounce;
        _pauseGate = pauseGate;
        _onChanges = onChanges;
        _backgroundTasks = backgroundTasks;
        _logger = logger;
        _operationName = operationName;

        _watcher = new FileSystemWatcher
        {
            Path = path,
            InternalBufferSize = InternalBufferSize,
            NotifyFilter = NotifyFilters.CreationTime
                | NotifyFilters.LastWrite
                | NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.Size,
            Filter = "*.*",
            IncludeSubdirectories = includeSubdirectories,
        };

        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        if (watchModifications)
        {
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Renamed += OnRenamed;
        }

        _watcher.EnableRaisingEvents = true;
    }

    public void SuspendEvents()
    {
        _watcher.EnableRaisingEvents = false;
    }

    public void ResumeEvents()
    {
        _watcher.EnableRaisingEvents = true;
    }

    public Dictionary<string, WatcherChange> SnapshotChanges()
    {
        lock (_changes)
        {
            return _changes.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (Directory.Exists(e.FullPath)) return;
        if (!SupportedFileTypes.IsAllowedPath(e.FullPath)) return;
        if (e.ChangeType is not (WatcherChangeTypes.Changed or WatcherChangeTypes.Deleted or WatcherChangeTypes.Created))
            return;

        _logger.LogTrace("FSW {event}: {path}", e.ChangeType, e.FullPath);

        lock (_changes)
        {
            _changes[e.FullPath] = new(e.ChangeType);
        }

        KickDrain();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (Directory.Exists(e.FullPath))
        {
            var directoryFiles = Directory.GetFiles(e.FullPath, "*.*", SearchOption.AllDirectories);
            lock (_changes)
            {
                foreach (var file in directoryFiles)
                {
                    if (!SupportedFileTypes.IsAllowedPath(file)) continue;
                    var oldPath = file.Replace(e.FullPath, e.OldFullPath, StringComparison.OrdinalIgnoreCase);

                    _changes.Remove(oldPath);
                    _changes[file] = new(WatcherChangeTypes.Renamed, oldPath);
                    _logger.LogTrace("FSW Renamed: {path} -> {new}", oldPath, file);
                }
            }
        }
        else
        {
            if (!SupportedFileTypes.IsAllowedPath(e.FullPath)) return;

            lock (_changes)
            {
                _changes.Remove(e.OldFullPath);
                _changes[e.FullPath] = new(WatcherChangeTypes.Renamed, e.OldFullPath);
            }

            _logger.LogTrace("FSW Renamed: {path} -> {new}", e.OldFullPath, e.FullPath);
        }

        KickDrain();
    }

    private void KickDrain()
    {
        _ = _backgroundTasks.Run(DrainAsync, _operationName);
    }

    private async Task DrainAsync()
    {
        using var scope = _drainFlight.Begin();
        var token = scope.Token;

        Dictionary<string, WatcherChange> changes;
        lock (_changes)
            changes = _changes.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);

        try
        {
            do
            {
                await Task.Delay(_debounce, token).ConfigureAwait(false);
            } while (_pauseGate.IsPaused);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        lock (_changes)
        {
            foreach (var key in changes.Keys)
            {
                _changes.Remove(key);
            }
        }

        _onChanges(changes);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _drainFlight.Dispose();
    }
}
