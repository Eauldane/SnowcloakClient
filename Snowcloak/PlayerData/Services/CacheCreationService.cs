using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Comparer;
using Snowcloak.API.Data.Enum;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Objects;
using Snowcloak.Game.Scheduling;
using Snowcloak.Interop.Ipc;
using Snowcloak.PlayerData.Data;
using Snowcloak.PlayerData.Factories;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using ApiCharacterData = Snowcloak.API.Data.CharacterData;
using OwnCharacterData = Snowcloak.PlayerData.Data.CharacterData;

namespace Snowcloak.PlayerData.Services;

public sealed class CacheCreationService : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private static readonly TimeSpan BuildDebounceDelay = TimeSpan.FromSeconds(1);
    private readonly ApiController _apiController;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly SemaphoreSlim _cacheCreateLock = new(1);
    private readonly HashSet<ObjectKind> _cachesToCreate = [];
    private readonly SingleFlightCts _creationFlight = new();
    private readonly DalamudUtilService _dalamudUtil;
    private readonly HashSet<ObjectKind> _debouncedObjectCache = [];
    private readonly SingleFlightCts _debounceFlight = new();
    private readonly FileUploadManager _fileTransferManager;
    private readonly HashSet<PairHandler> _newVisiblePlayers = [];
    private readonly PairManager _pairManager;
    private readonly ConcurrentDictionary<ObjectKind, GameObjectHandler> _playerRelatedObjects = [];
    private readonly OwnCharacterData _playerData = new();
    private readonly HashSet<UserData> _pendingVisibleUsers = new(UserDataComparer.Instance);
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly Lock _visibilityLock = new();
    private bool _haltCharaDataCreation;
    private bool _isZoning;
    private ApiCharacterData? _lastSentData;
    private readonly IFrameTickHandle _onlineTick;
    private readonly IFrameTickHandle _cacheTick;
    private int _disposed;

    public CacheCreationService(
        ILogger<CacheCreationService> logger,
        SnowMediator mediator,
        GameObjectHandlerFactory gameObjectHandlerFactory,
        SnapshotBuilder snapshotBuilder,
        DalamudUtilService dalamudUtil,
        IFrameScheduler frameScheduler,
        ApiController apiController,
        PairManager pairManager,
        FileUploadManager fileTransferManager)
        : base(logger, mediator)
    {
        _snapshotBuilder = snapshotBuilder;
        _dalamudUtil = dalamudUtil;
        _apiController = apiController;
        _pairManager = pairManager;
        _fileTransferManager = fileTransferManager;
        _backgroundTasks = new BackgroundTaskTracker(logger);

        Mediator.Subscribe<ZoneSwitchStartMessage>(this, _ => _isZoning = true);
        Mediator.Subscribe<ZoneSwitchEndMessage>(this, _ => _isZoning = false);
        Mediator.Subscribe<HaltCharaDataCreation>(this, msg => _haltCharaDataCreation = !msg.Resume);
        Mediator.Subscribe<CreateCacheForObjectMessage>(this, msg => QueueObjectBuild(msg.ObjectToCreateFor.ObjectKind, $"handler {msg.ObjectToCreateFor}"));
        Mediator.Subscribe<ClearCacheForObjectMessage>(this, OnClearCacheForObject);
        Mediator.Subscribe<ClassJobChangedMessage>(this, OnClassJobChanged);
        Mediator.Subscribe<CustomizePlusMessage>(this, OnCustomizePlusChanged);
        Mediator.Subscribe<HeelsOffsetMessage>(this, _ => OnHeelsOffsetChanged());
        Mediator.Subscribe<GlamourerChangedMessage>(this, OnGlamourerChanged);
        Mediator.Subscribe<HonorificMessage>(this, OnHonorificChanged);
        Mediator.Subscribe<MoodlesMessage>(this, OnMoodlesChanged);
        Mediator.Subscribe<PetNamesMessage>(this, OnPetNamesChanged);
        Mediator.Subscribe<OptionalIpcAvailabilityChangedMessage>(this, OnOptionalIpcAvailabilityChanged);
        Mediator.Subscribe<PenumbraModSettingChangedMessage>(this, _ => QueueAllBuilds("Penumbra Mod settings change"));
        Mediator.Subscribe<PlayerChangedMessage>(this, _ => PushCharacterData(_pairManager.GetVisibleUsers()));
        Mediator.Subscribe<PairHandlerVisibleMessage>(this, msg => OnPairHandlerVisible(msg.Player));
        Mediator.Subscribe<ConnectedMessage>(this, _ => OnConnected());

        _playerRelatedObjects[ObjectKind.Player] = gameObjectHandlerFactory.Create(ObjectKind.Player, dalamudUtil.GetPlayerPointer, isWatched: true)
            .GetAwaiter().GetResult();
        _playerRelatedObjects[ObjectKind.MinionOrMount] = gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => dalamudUtil.GetMinionOrMount(), isWatched: true)
            .GetAwaiter().GetResult();
        _playerRelatedObjects[ObjectKind.Pet] = gameObjectHandlerFactory.Create(ObjectKind.Pet, () => dalamudUtil.GetPet(), isWatched: true)
            .GetAwaiter().GetResult();
        _playerRelatedObjects[ObjectKind.Companion] = gameObjectHandlerFactory.Create(ObjectKind.Companion, () => dalamudUtil.GetCompanion(), isWatched: true)
            .GetAwaiter().GetResult();

        _cacheTick = frameScheduler.Register("CacheCreation", TickInterval.EveryFrame, TickPriority.Normal, ProcessCacheCreation,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
        _onlineTick = frameScheduler.Register("OnlinePlayers", TickInterval.EveryMilliseconds(200), TickPriority.Normal, ProcessVisiblePlayers,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cacheTick.Dispose();
        _onlineTick.Dispose();
        base.Dispose(disposing);

        CancelBackgroundWork();
        _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(2), nameof(CacheCreationService));
        DisposeOwnedResources();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cacheTick.Dispose();
        _onlineTick.Dispose();
        base.Dispose(disposing: true);
        CancelBackgroundWork();
        await _backgroundTasks.StopAsync().ConfigureAwait(false);
        DisposeOwnedResources();
        GC.SuppressFinalize(this);
    }

    private void QueueObjectBuild(ObjectKind kind, string reason)
    {
        Logger.LogDebug("Queueing {kind} cache build from {reason}", kind, reason);
        AddCacheToCreate(kind);
    }

    private void QueueAllBuilds(string reason)
    {
        Logger.LogDebug("Queueing all cache builds from {reason}", reason);
        AddCacheToCreate(ObjectKind.Player);
        AddCacheToCreate(ObjectKind.Pet);
        AddCacheToCreate(ObjectKind.MinionOrMount);
        AddCacheToCreate(ObjectKind.Companion);
    }

    private void OnClearCacheForObject(ClearCacheForObjectMessage msg)
    {
        if (msg.ObjectToCreateFor.ObjectKind == ObjectKind.Pet)
        {
            Logger.LogTrace("Received clear cache for {obj}, ignoring", msg.ObjectToCreateFor);
            return;
        }

        Logger.LogDebug("Clearing cache for {obj}", msg.ObjectToCreateFor);
        AddCacheToCreate(msg.ObjectToCreateFor.ObjectKind);
    }

    private void OnClassJobChanged(ClassJobChangedMessage msg)
    {
        if (!_playerRelatedObjects.TryGetValue(ObjectKind.Player, out var playerHandler))
        {
            if (msg.GameObjectHandler.ObjectKind != ObjectKind.Player)
            {
                return;
            }
        }
        else if (msg.GameObjectHandler != playerHandler)
        {
            return;
        }

        AddCacheToCreate(ObjectKind.Player);
        AddCacheToCreate(ObjectKind.Pet);
    }

    private void OnCustomizePlusChanged(CustomizePlusMessage msg)
    {
        if (_isZoning)
        {
            return;
        }

        foreach (var item in _playerRelatedObjects.Where(item => msg.Address == null || item.Value.Address == msg.Address).Select(k => k.Key))
        {
            Logger.LogDebug("Received CustomizePlus change, updating {obj}", item);
            AddCacheToCreate(item);
        }
    }

    private void OnHeelsOffsetChanged()
    {
        if (_isZoning)
        {
            return;
        }

        Logger.LogDebug("Received Heels Offset change, updating player");
        AddCacheToCreate(ObjectKind.Player);
    }

    private void OnGlamourerChanged(GlamourerChangedMessage msg)
    {
        if (_isZoning)
        {
            return;
        }

        if (TryFindObjectKind(msg.Address, out var changedType))
        {
            Logger.LogDebug("Received GlamourerChangedMessage for {kind}", changedType);
            AddCacheToCreate(changedType);
        }
    }

    private void OnHonorificChanged(HonorificMessage msg)
    {
        if (_isZoning)
        {
            return;
        }

        if (!string.Equals(msg.NewHonorificTitle, _playerData.HonorificData, StringComparison.Ordinal))
        {
            Logger.LogDebug("Received Honorific change, updating player");
            AddCacheToCreate(ObjectKind.Player);
        }
    }

    private void OnMoodlesChanged(MoodlesMessage msg)
    {
        if (_isZoning)
        {
            return;
        }

        if (TryFindObjectKind(msg.Address, out var changedType) && changedType == ObjectKind.Player)
        {
            Logger.LogDebug("Received Moodles change, updating player");
            AddCacheToCreate(ObjectKind.Player);
        }
    }

    private void OnPetNamesChanged(PetNamesMessage msg)
    {
        if (_isZoning)
        {
            return;
        }

        if (!string.Equals(msg.PetNicknamesData, _playerData.PetNamesData, StringComparison.Ordinal))
        {
            Logger.LogDebug("Received Pet Nicknames change, updating player");
            AddCacheToCreate(ObjectKind.Player);
        }
    }

    private void OnOptionalIpcAvailabilityChanged(OptionalIpcAvailabilityChangedMessage msg)
    {
        if (_isZoning)
        {
            return;
        }

        Logger.LogDebug("Optional IPC {ipc} availability changed to {available}, rebuilding local character data", msg.IpcName, msg.IsAvailable);

        if (string.Equals(msg.IpcName, IpcManager.CustomizePlusIpcName, StringComparison.Ordinal))
        {
            foreach (var objectKind in _playerRelatedObjects.Keys)
            {
                AddCacheToCreate(objectKind);
            }

            return;
        }

        AddCacheToCreate(ObjectKind.Player);
    }

    private void AddCacheToCreate(ObjectKind kind = ObjectKind.Player)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var scope = _debounceFlight.Begin(_runtimeCts.Token);
        var token = scope.Token;
        try
        {
            _cacheCreateLock.Wait(token);
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        try
        {
            _debouncedObjectCache.Add(kind);
        }
        finally
        {
            _cacheCreateLock.Release();
        }

        _ = _backgroundTasks.Run(async () =>
        {
            using (scope)
            {
                try
                {
                    await Task.Delay(BuildDebounceDelay, token).ConfigureAwait(false);
                    await _cacheCreateLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        Logger.LogTrace("Debounce complete, inserting objects to create for: {obj}", string.Join(", ", _debouncedObjectCache));
                        foreach (var item in _debouncedObjectCache)
                        {
                            _cachesToCreate.Add(item);
                        }

                        _debouncedObjectCache.Clear();
                    }
                    finally
                    {
                        _cacheCreateLock.Release();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Logger.LogTrace("Cache creation debounce cancelled");
                }
            }
        }, nameof(AddCacheToCreate));
    }

    private void ProcessCacheCreation()
    {
        if (Volatile.Read(ref _disposed) != 0 || _isZoning || _haltCharaDataCreation)
        {
            return;
        }

        if (_playerRelatedObjects.Any(p => p.Value.CurrentDrawCondition is
            not (GameObjectDrawCondition.None or GameObjectDrawCondition.DrawObjectZero or GameObjectDrawCondition.ObjectZero)))
        {
            Logger.LogDebug("Waiting for draw to finish before executing cache creation");
            return;
        }

        _cacheCreateLock.Wait(_runtimeCts.Token);

        List<ObjectKind> objectKindsToCreate;
        try
        {
            if (_cachesToCreate.Count == 0)
            {
                return;
            }

            objectKindsToCreate = _cachesToCreate.ToList();
            _cachesToCreate.Clear();
        }
        finally
        {
            _cacheCreateLock.Release();
        }

        var scope = _creationFlight.Begin(_runtimeCts.Token);
        var creationToken = scope.Token;
        _ = _backgroundTasks.Run(async () =>
        {
            using (scope)
            {
                await CreateCharacterData(objectKindsToCreate, creationToken).ConfigureAwait(false);
            }
        }, nameof(ProcessCacheCreation));
    }

    private async Task CreateCharacterData(List<ObjectKind> objectKindsToCreate, CancellationToken creationToken)
    {
        try
        {
            Logger.LogDebug("Creating Caches for {objectKinds}", string.Join(", ", objectKindsToCreate));

            Dictionary<ObjectKind, CharacterDataFragment?> createdData = [];
            foreach (var objectKind in objectKindsToCreate)
            {
                if (!_playerRelatedObjects.TryGetValue(objectKind, out var handler))
                {
                    Logger.LogDebug("Skipping {objectKind} cache build because the owned handler is not ready", objectKind);
                    continue;
                }

                createdData[objectKind] = await _snapshotBuilder.BuildCharacterData(handler, creationToken).ConfigureAwait(false);
            }

            foreach (var kvp in createdData)
            {
                _playerData.SetFragment(kvp.Key, kvp.Value);
            }

            var apiData = _playerData.ToAPI();
            OnCharacterDataBuilt(apiData);
            Mediator.Publish(new CharacterDataCreatedMessage(apiData));
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Cache Creation cancelled");
        }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Error during Cache Creation Processing");
        }
        finally
        {
            Logger.LogDebug("Cache Creation complete");
        }
    }

    private void OnPairHandlerVisible(PairHandler player)
    {
        lock (_visibilityLock)
        {
            if (_lastSentData == null)
            {
                _pendingVisibleUsers.Add(player.Pair.UserData);
            }

            _newVisiblePlayers.Add(player);
        }
    }

    private void OnCharacterDataBuilt(ApiCharacterData newData)
    {
        List<UserData>? pendingVisibleUsers = null;
        lock (_visibilityLock)
        {
            if (_lastSentData != null && string.Equals(newData.DataHash.Value, _lastSentData.DataHash.Value, StringComparison.Ordinal))
            {
                Logger.LogDebug("Not sending data for {hash}", newData.DataHash.Value);
                return;
            }

            _lastSentData = newData;
            if (_pendingVisibleUsers.Count > 0)
            {
                pendingVisibleUsers = _pendingVisibleUsers.ToList();
                _pendingVisibleUsers.Clear();
            }
        }

        var uploadableReplacementCount = newData.FileReplacements.Sum(kvp => kvp.Value.Count(v => string.IsNullOrEmpty(v.FileSwapPath)));
        var fileSwapCount = newData.FileReplacements.Sum(kvp => kvp.Value.Count(v => !string.IsNullOrEmpty(v.FileSwapPath)));
        Logger.LogInformation(
            "Built local character data {hash}: objects={objectCount}, uploadableFiles={uploadableReplacementCount}, fileSwaps={fileSwapCount}, glamourerEntries={glamourerCount}",
            newData.DataHash.Value,
            newData.FileReplacements.Count,
            uploadableReplacementCount,
            fileSwapCount,
            newData.GlamourerData.Count);
        Logger.LogDebug("Pushing updated character data");

        var visibleUsers = _pairManager.GetVisibleUsers();
        if (pendingVisibleUsers != null)
        {
            visibleUsers = visibleUsers.Union(pendingVisibleUsers, UserDataComparer.Instance).ToList();
        }

        PushCharacterData(visibleUsers);
    }

    private void OnConnected()
    {
        ApiCharacterData? lastSentData;
        int pendingVisibleUserCount;
        lock (_visibilityLock)
        {
            lastSentData = _lastSentData;
            pendingVisibleUserCount = _pendingVisibleUsers.Count;
        }

        var visibleUsers = _pairManager.GetVisibleUsers();
        if (lastSentData == null)
        {
            Logger.LogInformation(
                "Connected to server but no cached local character data is available yet; skipping initial push. Visible users={visibleUserCount}, pending visible users={pendingVisibleUserCount}",
                visibleUsers.Count,
                pendingVisibleUserCount);
            return;
        }

        Logger.LogInformation("Connected to server, pushing cached local character data {hash} to {visibleUserCount} visible users",
            lastSentData.DataHash.Value, visibleUsers.Count);
        PushCharacterData(visibleUsers);
    }

    private void ProcessVisiblePlayers()
    {
        if (!_dalamudUtil.GetIsPlayerPresent() || !_apiController.IsConnected)
        {
            return;
        }

        List<PairHandler> newVisiblePlayers;
        lock (_visibilityLock)
        {
            if (_newVisiblePlayers.Count == 0)
            {
                return;
            }

            newVisiblePlayers = _newVisiblePlayers.ToList();
            _newVisiblePlayers.Clear();
        }

        Logger.LogTrace("Has new visible players, requesting cached character data and pushing local character data");

        var visiblePlayerIdents = newVisiblePlayers
            .Select(player => player.Pair.Ident)
            .Where(ident => !string.IsNullOrWhiteSpace(ident))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var visibleUsers = newVisiblePlayers
            .Select(player => player.Pair.UserData)
            .Distinct(UserDataComparer.Instance)
            .ToList();

        RequestCharacterData(visiblePlayerIdents);
        PushCharacterData(visibleUsers);
    }

    private void PushCharacterData(List<UserData> visiblePlayers)
    {
        ApiCharacterData? data;
        lock (_visibilityLock)
        {
            data = _lastSentData;
        }

        if (data == null)
        {
            return;
        }

        _ = _backgroundTasks.Run(
            ct => PushCharacterDataInternal(data.Clone(), visiblePlayers, ct),
            nameof(PushCharacterData),
            _runtimeCts.Token);
    }

    private async Task PushCharacterDataInternal(ApiCharacterData data, List<UserData> visiblePlayers, CancellationToken cancellationToken)
    {
        try
        {
            var dataToSend = await _fileTransferManager.UploadFiles(data, visiblePlayers, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _apiController.PushCharacterData(dataToSend, visiblePlayers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Character data upload was cancelled");
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "FileTransferManager is not initialized", StringComparison.Ordinal))
        {
            Logger.LogDebug("Skipping character data upload because file transfers are not initialized yet");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unexpected exception while pushing character data");
        }
    }

    private void RequestCharacterData(List<string> visiblePlayerIdents)
    {
        if (visiblePlayerIdents.Count == 0)
        {
            return;
        }

        _ = _backgroundTasks.Run(
            ct => RequestCharacterDataInternal(visiblePlayerIdents, ct),
            nameof(RequestCharacterData),
            _runtimeCts.Token);
    }

    private async Task RequestCharacterDataInternal(List<string> visiblePlayerIdents, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _apiController.UserGetPairsInRange(visiblePlayerIdents).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger.LogDebug("Cached character data request was cancelled");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unexpected exception while requesting cached character data");
        }
    }

    private bool TryFindObjectKind(nint address, out ObjectKind objectKind)
    {
        foreach (var (candidateKind, handler) in _playerRelatedObjects)
        {
            if (handler.Address == address)
            {
                objectKind = candidateKind;
                return true;
            }
        }

        objectKind = default;
        return false;
    }

    private void CancelBackgroundWork()
    {
        _backgroundTasks.StopAccepting();
        _runtimeCts.Cancel();
        _creationFlight.Cancel();
        _debounceFlight.Cancel();
    }

    private void DisposeOwnedResources()
    {
        _playerRelatedObjects.Values.ToList().ForEach(p => p.Dispose());
        _runtimeCts.Dispose();
        _creationFlight.Dispose();
        _debounceFlight.Dispose();
        _cacheCreateLock.Dispose();
    }
}
