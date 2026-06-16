using Microsoft.Extensions.Logging;
using ElezenTools.Core.Async;
using Snowcloak.Interop.Ipc;
using Snowcloak.PlayerData.Factories;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Handlers;

internal sealed class PairVisibilityTracker
{
    private readonly PairHandler _handler;
    private readonly ILogger Logger;
    private readonly SnowMediator Mediator;
    private readonly Pair Pair;
    private readonly PairAppliedState _appliedState;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly SingleFlightCts _downloadFlight;
    private readonly VisibilityService _visibilityService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly NotesStore _notesStore;
    private readonly IpcManager _ipcManager;

    public PairVisibilityTracker(PairHandler handler, ILogger logger, SnowMediator mediator, Pair pair,
        PairAppliedState appliedState, BackgroundTaskTracker backgroundTasks, SingleFlightCts downloadFlight,
        VisibilityService visibilityService, DalamudUtilService dalamudUtil, GameObjectHandlerFactory gameObjectHandlerFactory,
        NotesStore notesStore, IpcManager ipcManager)
    {
        _handler = handler;
        Logger = logger;
        Mediator = mediator;
        Pair = pair;
        _appliedState = appliedState;
        _backgroundTasks = backgroundTasks;
        _downloadFlight = downloadFlight;
        _visibilityService = visibilityService;
        _dalamudUtil = dalamudUtil;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _notesStore = notesStore;
        _ipcManager = ipcManager;
    }

    public void RetryDeferredApplicationIfReady()
    {
        var applicationBase = _handler.Deferred;
        var data = _appliedState.CachedData;
        if (applicationBase == Guid.Empty || data == null) return;

        var handler = _handler.CharaHandler;
        bool ready = handler != null && handler.Address != nint.Zero;
        if (!ready)
        {
            _handler.ApplyAttemptedWhileReady = false;
            return;
        }

        if (_handler.ApplyAttemptedWhileReady) return;
        _handler.ApplyAttemptedWhileReady = true;

        _ = _backgroundTasks.Run(() =>
        {
            _handler.ApplyCharacterData(applicationBase, data, forceApplyCustomization: true);
            return Task.CompletedTask;
        }, nameof(PairHandler.ApplyCharacterData));
    }

    public void UpdateVisibility(bool nowVisible, bool invalidate = false)
    {
        if (string.IsNullOrEmpty(_handler.PlayerName))
        {
            var pc = _dalamudUtil.FindPlayerByNameHash(Pair.Ident);
            if (pc.EntityId == 0)
            {
                Logger.LogDebug("Visibility update skipped for {this}, player not found yet; rearming tracking", _handler);
                _visibilityService.StopTracking(Pair.Ident);
                _visibilityService.StartTracking(Pair.Ident);
                return;
            }
            Logger.LogDebug("One-Time Initializing {this}", _handler);
            Initialize(pc.Name);
            Logger.LogDebug("One-Time Initialized {this}", _handler);
            Mediator.Publish(new EventMessage(new Event(_handler.PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
                $"Initializing User For Character {pc.Name}")));
        }
        
        if (!nowVisible && invalidate)
        {
            bool wasVisible = _handler.IsVisible;
            _handler.Reverter.QueueClearPlayerScopedOptionalData(Guid.NewGuid());
            _handler.IsVisible = false;
            _handler.CharaHandler?.Invalidate();
            _downloadFlight.Cancel();
            Pair.ClearAutoPaused();
            if (wasVisible)
                Logger.LogTrace("{this} visibility changed, now: {visi}", _handler, _handler.IsVisible);
            Logger.LogDebug("Invalidating {this}", _handler);
            _handler.Reverter.UndoApplication();
            return;
        }

        if (!_handler.IsVisible && nowVisible)
        {
            _handler.IsVisible = true;
            Mediator.Publish(new PairHandlerVisibleMessage(_handler));
            if (_appliedState.CachedData != null)
            {
                Guid appData = _handler.Deferred != Guid.Empty ? _handler.Deferred : Guid.NewGuid();
                using (Logger.BeginScope("BASE-{ApplicationBase}", appData))
                    Logger.LogTrace("{this} visibility changed, now: {visi}, cached data exists", _handler, _handler.IsVisible);
                
                _handler.ApplyAttemptedWhileReady = true;
                _ = _backgroundTasks.Run(() =>
                {
                    _handler.ApplyCharacterData(appData, _appliedState.CachedData!, forceApplyCustomization: true);
                    return Task.CompletedTask;
                }, nameof(PairHandler.ApplyCharacterData));
            }
            else
            {
                Logger.LogTrace("{this} visibility changed, now: {visi}, no cached data exists", _handler, _handler.IsVisible);
            }
        }
        else if (_handler.IsVisible && !nowVisible)
        {
            _handler.Reverter.QueueClearPlayerScopedOptionalData(Guid.NewGuid());
            _handler.IsVisible = false;
            _handler.CharaHandler?.Invalidate();
            _downloadFlight.Cancel();
            Pair.ClearAutoPaused();
            Logger.LogTrace("{this} visibility changed, now: {visi}", _handler, _handler.IsVisible);
        }
    }

    private void Initialize(string name)
    {
        _handler.PlayerName = name;
        _handler.CharaHandler = _gameObjectHandlerFactory.Create(ObjectKind.Player, () => _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident), isWatched: false).GetAwaiter().GetResult();

        _notesStore.AutofillNoteWithCharacterName(Pair.UserData.UID, name);

        Mediator.Subscribe<HonorificReadyMessage>(_handler, msg =>
        {
            if (string.IsNullOrEmpty(_appliedState.CachedData?.HonorificData)) return;
            Logger.LogTrace("Reapplying Honorific data for {this}", _handler);
            _ = _backgroundTasks.Run(async () => await _ipcManager.Honorific.SetTitleAsync(_handler.PlayerCharacter, _appliedState.CachedData.HonorificData).ConfigureAwait(false), nameof(HonorificReadyMessage));
        });

        Mediator.Subscribe<PetNamesReadyMessage>(_handler, msg =>
        {
            if (string.IsNullOrEmpty(_appliedState.CachedData?.PetNamesData)) return;
            Logger.LogTrace("Reapplying Pet Names data for {this}", _handler);
            _ = _backgroundTasks.Run(async () => await _ipcManager.PetNames.SetPlayerData(_handler.PlayerCharacter, _appliedState.CachedData.PetNamesData).ConfigureAwait(false), nameof(PetNamesReadyMessage));
        });
    }
}
