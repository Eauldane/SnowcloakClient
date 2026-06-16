using Snowcloak.API.Data.Enum;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Configurations;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.Infrastructure.Data;
using Snowcloak.PlayerData.Data;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using System.Collections.Concurrent;

namespace Snowcloak.FileCache;

public sealed class TransientResourceManager : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly Lock _cacheAdditionLock = new();
    private readonly HashSet<string> _cachedHandledPaths = new(StringComparer.Ordinal);
    private readonly DalamudUtilService _dalamudUtil;
    private readonly string[] _handledFileTypes = ["tmb", "pap", "avfx", "atex", "sklb", "eid", "phyb", "scd", "skp", "shpk"];
    private readonly string[] _handledRecordingFileTypes = ["tex", "mdl", "mtrl"];
    private readonly TransientConfigService _legacyConfigurationService;
    private readonly HashSet<GameObjectHandler> _playerRelatedPointers = [];
    private readonly TransientRecordingService _recordingService;
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly SingleFlightCts _sendTransientFlight = new();
    private readonly SemiTransientResourceStore _semiTransientStore;
    private ConcurrentDictionary<IntPtr, ObjectKind> _cachedFrameAddresses = [];
    private ConcurrentDictionary<ObjectKind, HashSet<string>>? _semiTransientResources;
    private uint _lastClassJobId = uint.MaxValue;
    private string? _lastPlayerPersistentDataKey;
    private bool _legacyTransientDataImported;
    private readonly Lock _playerPointersLock = new();
    private readonly IFrameTickHandle _tick;
    private int _disposed;
    public bool IsTransientRecording => _recordingService.IsRecording;

    public TransientResourceManager(ILogger<TransientResourceManager> logger, TransientConfigService legacyConfigurationService,
            DalamudUtilService dalamudUtil, SnowMediator mediator, IFrameScheduler frameScheduler,
            SemiTransientResourceStore semiTransientStore, TransientRecordingService recordingService) : base(logger, mediator)
    {
        ArgumentNullException.ThrowIfNull(frameScheduler);

        _legacyConfigurationService = legacyConfigurationService;
        _dalamudUtil = dalamudUtil;
        _semiTransientStore = semiTransientStore;
        _recordingService = recordingService;
        _backgroundTasks = new BackgroundTaskTracker(logger);

        Mediator.Subscribe<PenumbraResourceLoadMessage>(this, Manager_PenumbraResourceLoadEvent);
        Mediator.Subscribe<PenumbraModSettingChangedMessage>(this, (_) => Manager_PenumbraModSettingChanged());
        _tick = frameScheduler.Register("TransientResource", TickInterval.EveryFrame, TickPriority.High, DalamudUtil_FrameworkUpdate,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
        Mediator.Subscribe<GameObjectHandlerCreatedMessage>(this, (msg) =>
        {
            if (!msg.OwnedObject) return;
            lock (_playerPointersLock)
            {
                _playerRelatedPointers.Add(msg.GameObjectHandler);
            }
        });
        Mediator.Subscribe<GameObjectHandlerDestroyedMessage>(this, (msg) =>
        {
            if (!msg.OwnedObject) return;
            lock (_playerPointersLock)
            {
                _playerRelatedPointers.Remove(msg.GameObjectHandler);
            }
        });
    }

    private string PlayerPersistentDataKey => _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult() + "_" + _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult();
    private ConcurrentDictionary<ObjectKind, HashSet<string>> SemiTransientResources
    {
        get
        {
            if (_semiTransientResources == null)
            {
                _semiTransientResources = LoadSemiTransientResources();
            }

            return _semiTransientResources;
        }
    }
    private ConcurrentDictionary<ObjectKind, HashSet<string>> TransientResources { get; } = new();

    private ConcurrentDictionary<ObjectKind, HashSet<string>> LoadSemiTransientResources()
    {
        EnsureLegacyTransientDataImported();

        var characterKey = PlayerPersistentDataKey;
        _lastPlayerPersistentDataKey = characterKey;
        var jobId = _dalamudUtil.ClassJobId;
        return new ConcurrentDictionary<ObjectKind, HashSet<string>>
        {
            [ObjectKind.Player] = _semiTransientStore.Load(characterKey, (int)ObjectKind.Player, jobId),
            [ObjectKind.Pet] = _semiTransientStore.Load(characterKey, (int)ObjectKind.Pet, jobId),
        };
    }

    private void EnsureLegacyTransientDataImported()
    {
        if (_legacyTransientDataImported)
        {
            return;
        }

        foreach (var (characterKey, config) in _legacyConfigurationService.Current.TransientConfigs)
        {
            _semiTransientStore.ImportLegacy(
                characterKey,
                (int)ObjectKind.Player,
                (int)ObjectKind.Pet,
                config.GlobalPersistentCache,
                config.JobSpecificCache,
                config.JobSpecificPetCache,
                DateTime.UtcNow);
        }

        _legacyTransientDataImported = true;
    }

    public void CleanUpSemiTransientResources(ObjectKind objectKind, IReadOnlyCollection<FileReplacement>? fileReplacement = null)
    {
        if (!SemiTransientResources.TryGetValue(objectKind, out HashSet<string>? value))
            return;

        if (fileReplacement == null)
        {
            value.Clear();
            return;
        }

        var candidates = fileReplacement.Where(p => !p.HasFileReplacement).SelectMany(p => p.GamePaths).ToList();
        if (candidates.Count == 0)
            return;

        int removedPaths = _semiTransientStore.RemovePaths(PlayerPersistentDataKey, (int)objectKind, candidates);
        foreach (var replacement in candidates)
        {
            value.Remove(replacement);
        }

        if (removedPaths > 0)
        {
            Logger.LogTrace("Removed {amount} of SemiTransient paths during CleanUp, Saving from {name}", removedPaths, nameof(CleanUpSemiTransientResources));
        }
    }

    public HashSet<string> GetSemiTransientResources(ObjectKind objectKind)
    {
        SemiTransientResources.TryGetValue(objectKind, out var result);

        return result ?? new HashSet<string>(StringComparer.Ordinal);
    }

    public void PersistTransientResources(ObjectKind objectKind)
    {
        if (!SemiTransientResources.TryGetValue(objectKind, out HashSet<string>? semiTransientResources))
        {
            SemiTransientResources[objectKind] = semiTransientResources = new(StringComparer.Ordinal);
        }

        if (!TransientResources.TryGetValue(objectKind, out var resources))
        {
            return;
        }

        var transientResources = resources.ToList();
        Logger.LogDebug("Persisting {count} transient resources", transientResources.Count);
        List<string> newlyAddedGamePaths = resources.Except(semiTransientResources, StringComparer.Ordinal).ToList();
        foreach (var gamePath in transientResources)
        {
            semiTransientResources.Add(gamePath);
        }

        if (objectKind == ObjectKind.Player && newlyAddedGamePaths.Count != 0)
        {
            var jobId = _dalamudUtil.ClassJobId;
            _semiTransientStore.SavePlayerPaths(PlayerPersistentDataKey, (int)objectKind, jobId, newlyAddedGamePaths, DateTime.UtcNow);
        }
        else if (objectKind == ObjectKind.Pet && newlyAddedGamePaths.Count != 0)
        {
            var jobId = _dalamudUtil.ClassJobId;
            _semiTransientStore.SaveJobScopedPaths(PlayerPersistentDataKey, (int)objectKind, jobId, newlyAddedGamePaths, DateTime.UtcNow);
        }

        TransientResources[objectKind].Clear();
    }

    public void RemoveTransientResource(ObjectKind objectKind, string path)
    {
        if (SemiTransientResources.TryGetValue(objectKind, out var resources))
        {
            resources.RemoveWhere(f => string.Equals(path, f, StringComparison.Ordinal));
            if (objectKind == ObjectKind.Player)
            {
                _semiTransientStore.RemovePath(PlayerPersistentDataKey, (int)objectKind, path);
            }
        }
    }

    internal bool AddTransientResource(ObjectKind objectKind, string item)
    {
        if (SemiTransientResources.TryGetValue(objectKind, out var semiTransient) && semiTransient != null && semiTransient.Contains(item))
            return false;

        if (!TransientResources.TryGetValue(objectKind, out HashSet<string>? transientResource))
        {
            transientResource = new HashSet<string>(StringComparer.Ordinal);
            TransientResources[objectKind] = transientResource;
        }

        return transientResource.Add(item.ToLowerInvariant());
    }

    internal void ClearTransientPaths(ObjectKind objectKind, List<string> list)
    {
        // ignore all recording only datatypes
        int recordingOnlyRemoved = list.RemoveAll(entry => _handledRecordingFileTypes.Any(ext => entry.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
        if (recordingOnlyRemoved > 0)
        {
            Logger.LogTrace("Ignored {0} game paths when clearing transients", recordingOnlyRemoved);
        }

        var pathsToRemove = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pathsToRemove.Count == 0)
        {
            return;
        }

        if (TransientResources.TryGetValue(objectKind, out var set))
        {
            var removedTransientEntries = set.Where(pathsToRemove.Contains).ToList();
            foreach (var file in removedTransientEntries)
            {
                Logger.LogTrace("Removing From Transient: {file}", file);
            }

            int removed = set.RemoveWhere(pathsToRemove.Contains);
            Logger.LogDebug("Removed {removed} previously existing transient paths", removed);
        }

        bool reloadSemiTransient = false;
        if (objectKind == ObjectKind.Player && SemiTransientResources.TryGetValue(objectKind, out var semiset))
        {
            var removedSemiTransientEntries = semiset.Where(pathsToRemove.Contains).ToList();
            if (removedSemiTransientEntries.Count > 0)
            {
                foreach (var file in removedSemiTransientEntries)
                {
                    Logger.LogTrace("Removing From SemiTransient: {file}", file);
                }

                _semiTransientStore.RemovePaths(PlayerPersistentDataKey, (int)objectKind, removedSemiTransientEntries);
            }

            int removed = semiset.RemoveWhere(pathsToRemove.Contains);
            Logger.LogDebug("Removed {removed} previously existing semi transient paths", removed);
            if (removed > 0)
            {
                reloadSemiTransient = true;
            }
        }

        if (reloadSemiTransient)
            _semiTransientResources = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _tick.Dispose();
        base.Dispose(disposing);

        CancelBackgroundWork();
        _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(2), nameof(TransientResourceManager));
        DisposeOwnedResources();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _tick.Dispose();
        base.Dispose(disposing: true);
        CancelBackgroundWork();
        await _backgroundTasks.StopAsync().ConfigureAwait(false);
        DisposeOwnedResources();
        GC.SuppressFinalize(this);
    }

    private void DalamudUtil_FrameworkUpdate()
    {
        GameObjectHandler[] playerPointerSnapshot;
        lock (_playerPointersLock)
        {
            playerPointerSnapshot = _playerRelatedPointers.Where(k => k.Address != nint.Zero).ToArray();
        }

        _cachedFrameAddresses = new(playerPointerSnapshot.ToDictionary(c => c.Address, c => c.ObjectKind));
        lock (_cacheAdditionLock)
        {
            _cachedHandledPaths.Clear();
        }

        var playerPersistentDataKey = PlayerPersistentDataKey;
        if (_lastClassJobId != _dalamudUtil.ClassJobId || !string.Equals(_lastPlayerPersistentDataKey, playerPersistentDataKey, StringComparison.Ordinal))
        {
            _lastClassJobId = _dalamudUtil.ClassJobId;
            _lastPlayerPersistentDataKey = playerPersistentDataKey;
            _semiTransientResources = LoadSemiTransientResources();
        }

        foreach (var kind in Enum.GetValues(typeof(ObjectKind)))
        {
            if (!_cachedFrameAddresses.Any(k => k.Value == (ObjectKind)kind) && TransientResources.Remove((ObjectKind)kind, out _))
            {
                Logger.LogDebug("Object not present anymore: {kind}", kind.ToString());
            }
        }
    }

    private void Manager_PenumbraModSettingChanged()
    {
        Logger.LogDebug("Penumbra Mod Settings changed, verifying SemiTransientResources");
        GameObjectHandler[] playerPointerSnapshot;
        lock (_playerPointersLock)
        {
            playerPointerSnapshot = _playerRelatedPointers.ToArray();
        }

        foreach (var item in playerPointerSnapshot)
        {
            Mediator.Publish(new TransientResourceChangedMessage(item.Address));
        }
    }

    public void RebuildSemiTransientResources()
    {
        _semiTransientResources = null;
    }

    private void Manager_PenumbraResourceLoadEvent(PenumbraResourceLoadMessage msg)
    {
        var gamePath = msg.GamePath.ToLowerInvariant();
        var gameObjectAddress = msg.GameObject;
        var filePath = msg.FilePath;

        // ignore files already processed this frame
        if (_cachedHandledPaths.Contains(gamePath)) return;

        lock (_cacheAdditionLock)
        {
            _cachedHandledPaths.Add(gamePath);
        }

        // replace individual mtrl stuff
        if (filePath.StartsWith("|", StringComparison.OrdinalIgnoreCase))
        {
            filePath = filePath.Split("|")[2];
        }
        // replace filepath
        filePath = filePath.ToLowerInvariant().Replace("\\", "/", StringComparison.OrdinalIgnoreCase);

        // ignore files that are the same
        var replacedGamePath = gamePath.ToLowerInvariant().Replace("\\", "/", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(filePath, replacedGamePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // ignore files to not handle
        var handledTypes = IsTransientRecording ? _handledRecordingFileTypes.Concat(_handledFileTypes) : _handledFileTypes;
        if (!handledTypes.Any(type => gamePath.EndsWith(type, StringComparison.OrdinalIgnoreCase)))
        {
            lock (_cacheAdditionLock)
            {
                _cachedHandledPaths.Add(gamePath);
            }
            return;
        }

        // ignore files not belonging to anything player related
        if (!_cachedFrameAddresses.TryGetValue(gameObjectAddress, out var objectKind))
        {
            lock (_cacheAdditionLock)
            {
                _cachedHandledPaths.Add(gamePath);
            }
            return;
        }

        // ^ all of the code above is just to sanitize the data

        if (!TransientResources.TryGetValue(objectKind, out HashSet<string>? transientResources))
        {
            transientResources = new(StringComparer.OrdinalIgnoreCase);
            TransientResources[objectKind] = transientResources;
        }

        GameObjectHandler? owner;
        lock (_playerPointersLock)
        {
            owner = _playerRelatedPointers.FirstOrDefault(f => f.Address == gameObjectAddress);
        }
        bool alreadyTransient = false;

        bool transientContains = transientResources.Contains(replacedGamePath);
        bool semiTransientContains = SemiTransientResources.SelectMany(k => k.Value).Any(f => string.Equals(f, gamePath, StringComparison.OrdinalIgnoreCase));
        if (transientContains || semiTransientContains)
        {
            if (!IsTransientRecording)
                Logger.LogTrace("Not adding {replacedPath} => {filePath}, Reason: Transient: {contains}, SemiTransient: {contains2}", replacedGamePath, filePath,
                    transientContains, semiTransientContains);
            alreadyTransient = true;
        }
        else
        {
            if (!IsTransientRecording)
            {
                bool isAdded = transientResources.Add(replacedGamePath);
                if (isAdded)
                {
                    Logger.LogDebug("Adding {replacedGamePath} for {gameObject} ({filePath})", replacedGamePath, owner?.ToString() ?? gameObjectAddress.ToString("X"), filePath);
                    SendTransients(gameObjectAddress, objectKind);
                }
            }
        }

        if (owner != null && IsTransientRecording)
        {
            _recordingService.Record(owner, replacedGamePath, filePath, alreadyTransient);
        }
    }

    private void SendTransients(nint gameObject, ObjectKind objectKind)
    {
        var scope = _sendTransientFlight.Begin(_runtimeCts.Token);
        var token = scope.Token;
        _ = _backgroundTasks.Run(async () =>
        {
            using (scope)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    foreach (var kvp in TransientResources)
                    {
                        if (TransientResources.TryGetValue(objectKind, out var values) && values.Any())
                        {
                            Logger.LogTrace("Sending Transients for {kind}", objectKind);
                            Mediator.Publish(new TransientResourceChangedMessage(gameObject));
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Logger.LogTrace("Transient send debounce cancelled");
                }
            }
        }, nameof(SendTransients));
    }

    public void StartRecording(CancellationToken token)
    {
        _recordingService.StartRecording(token);
    }

    public async Task WaitForRecording(CancellationToken token)
    {
        await _recordingService.WaitForRecording(token).ConfigureAwait(false);
    }

    internal void SaveRecording()
    {
        HashSet<nint> addedTransients = [];
        foreach (var item in _recordingService.SaveRecording())
        {
            if (!TransientResources.TryGetValue(item.Owner.ObjectKind, out var transient))
            {
                TransientResources[item.Owner.ObjectKind] = transient = [];
            }

            Logger.LogTrace("Adding recorded: {gamePath} => {filePath}", item.GamePath, item.FilePath);

            transient.Add(item.GamePath);
            addedTransients.Add(item.Owner.Address);
        }

        foreach (var item in addedTransients)
        {
            Mediator.Publish(new TransientResourceChangedMessage(item));
        }
    }

    public IReadOnlyList<TransientRecord> RecordedTransients => _recordingService.RecordedTransients;

    public ValueProgress<TimeSpan> RecordTimeRemaining => _recordingService.TimeRemaining;

    private void CancelBackgroundWork()
    {
        _backgroundTasks.StopAccepting();
        _runtimeCts.Cancel();
        _sendTransientFlight.Cancel();
    }

    private void DisposeOwnedResources()
    {
        TransientResources.Clear();
        _semiTransientResources?.Clear();
        _runtimeCts.Dispose();
        _sendTransientFlight.Dispose();
    }
}
