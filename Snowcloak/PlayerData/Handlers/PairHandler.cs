using Snowcloak.API.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Data;
using Snowcloak.PlayerData.Factories;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.SnowcloakConfiguration;
using Snowcloak.Utils;
using Snowcloak.WebAPI.Files;
using System.Collections.Concurrent;
using System.Diagnostics;
using CharacterData = Snowcloak.API.Data.CharacterData;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Handlers;

public sealed class PairHandler : DisposableMediatorSubscriberBase
{
    private sealed record CombatData(Guid ApplicationId, CharacterData CharacterData, bool Forced);

    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileDownloadManager _downloadManager;
    private readonly FileCacheManager _fileDbManager;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly IpcManager _ipcManager;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager;
    private readonly VisibilityService _visibilityService;
    private CancellationTokenSource? _applicationCancellationTokenSource = new();
    private Guid _applicationId;
    private Task? _applicationTask;
    private CharacterData? _cachedData = null;
    private GameObjectHandler? _charaHandler;
    private readonly Dictionary<ObjectKind, Guid?> _customizeIds = [];
    private CombatData? _dataReceivedInDowntime;
    private CancellationTokenSource? _downloadCancellationTokenSource = new();
    private bool _forceApplyMods = false;
    private bool _isVisible;
    private Guid _deferred = Guid.Empty;
    private Guid _penumbraCollection = Guid.Empty;
    private bool _redrawOnNextApplication = false;
    private readonly DatabaseService _databaseService;

    public PairHandler(ILogger<PairHandler> logger, Pair pair, PairAnalyzer pairAnalyzer,
        GameObjectHandlerFactory gameObjectHandlerFactory,
        IpcManager ipcManager, FileDownloadManager transferManager,
        PluginWarningNotificationService pluginWarningNotificationManager,
        DalamudUtilService dalamudUtil, IHostApplicationLifetime lifetime,
        FileCacheManager fileDbManager, SnowMediator mediator,
        PlayerPerformanceService playerPerformanceService,
        ServerConfigurationManager serverConfigManager,
        SnowcloakConfigService configService, VisibilityService visibilityService, DatabaseService databaseService) : base(logger, mediator)
    {
        Pair = pair;
        PairAnalyzer = pairAnalyzer;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _ipcManager = ipcManager;
        _downloadManager = transferManager;
        _pluginWarningNotificationManager = pluginWarningNotificationManager;
        _dalamudUtil = dalamudUtil;
        _fileDbManager = fileDbManager;
        _playerPerformanceService = playerPerformanceService;
        _serverConfigManager = serverConfigManager;
        _configService = configService;
        _visibilityService = visibilityService;
        _databaseService = databaseService;
        _visibilityService.StartTracking(Pair.Ident);

        Mediator.SubscribeKeyed<PlayerVisibilityMessage>(this, Pair.Ident, (msg) => UpdateVisibility(msg.IsVisible, msg.Invalidate));

        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (_) =>
        {
            _downloadCancellationTokenSource?.CancelDispose();
            _charaHandler?.Invalidate();
            IsVisible = false;
        });
        Mediator.Subscribe<PenumbraInitializedMessage>(this, (_) =>
        {
            _penumbraCollection = Guid.Empty;
            if (!IsVisible && _charaHandler != null)
            {
                PlayerName = string.Empty;
                _charaHandler.Dispose();
                _charaHandler = null;
            }
        });
        Mediator.Subscribe<ClassJobChangedMessage>(this, (msg) =>
        {
            if (msg.GameObjectHandler == _charaHandler)
            {
                _redrawOnNextApplication = true;
            }
        });
        Mediator.Subscribe<CombatOrPerformanceEndMessage>(this, (msg) =>
        {
            if (IsVisible && _dataReceivedInDowntime != null)
            {
                ApplyCharacterData(_dataReceivedInDowntime.ApplicationId,
                    _dataReceivedInDowntime.CharacterData, _dataReceivedInDowntime.Forced);
                _dataReceivedInDowntime = null;
            }
        });
        Mediator.Subscribe<CombatOrPerformanceStartMessage>(this, _ =>
        {
            if (_configService.Current.HoldCombatApplication)
            {
                _dataReceivedInDowntime = null;
                _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate();
                _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate();
            }
        });
        Mediator.Subscribe<RecalculatePerformanceMessage>(this, (msg) =>
        {
            if (msg.UID != null && !msg.UID.Equals(Pair.UserData.UID, StringComparison.Ordinal)) return;
            Logger.LogDebug("Recalculating performance for {uid}", Pair.UserData.UID);
            pair.ApplyLastReceivedData(forced: true);
        });

        LastAppliedDataBytes = -1;
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                string text = "User Visibility Changed, now: " + (_isVisible ? "Is Visible" : "Is not Visible");
                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
                    EventSeverity.Informational, text)));
            }
        }
    }

    public long LastAppliedDataBytes { get; private set; }
    public Pair Pair { get; private init; }
    public PairAnalyzer PairAnalyzer { get; private init; }
    public nint PlayerCharacter => _charaHandler?.Address ?? nint.Zero;
    public unsafe uint PlayerCharacterId => (_charaHandler?.Address ?? nint.Zero) == nint.Zero
        ? uint.MaxValue
        : ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_charaHandler!.Address)->EntityId;
    public string? PlayerName { get; private set; }
    public string PlayerNameHash => Pair.Ident;

    public void ApplyCharacterData(Guid applicationBase, CharacterData characterData, bool forceApplyCustomization = false)
    {
        if (_configService.Current.HoldCombatApplication && _dalamudUtil.IsInCombatOrPerforming)
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: you are in combat or performing music, deferring application")));
            Logger.LogDebug("[BASE-{appBase}] Received data but player is in combat or performing", applicationBase);
            _dataReceivedInDowntime = new(applicationBase, characterData, forceApplyCustomization);
            SetUploading(isUploading: false);
            return;
        }

        if (_charaHandler == null || (PlayerCharacter == IntPtr.Zero))
        {
            if (_deferred != Guid.Empty)
            {
                _isVisible = false;
                _visibilityService.StopTracking(Pair.Ident);
                _visibilityService.StartTracking(Pair.Ident);
            }

            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: Receiving Player is in an invalid state, deferring application")));
            Logger.LogDebug("[BASE-{appBase}] Received data but player was in invalid state, charaHandlerIsNull: {charaIsNull}, playerPointerIsNull: {ptrIsNull}",
                applicationBase, _charaHandler == null, PlayerCharacter == IntPtr.Zero);
            var hasDiffMods = characterData.CheckUpdatedData(applicationBase, _cachedData, Logger,
                this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _forceApplyMods = hasDiffMods || _forceApplyMods || (PlayerCharacter == IntPtr.Zero && _cachedData == null);
            _cachedData = characterData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, characterData));
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase, _cachedData.DataHash.Value, _forceApplyMods);
            // Ensure that this deferred application actually occurs by forcing visibiltiy to re-proc
            // Set _deferred as a silencing flag to avoid spamming logs once per frame with failed applications
            _isVisible = false;
            _deferred = applicationBase;
            _visibilityService.StopTracking(Pair.Ident);
            _visibilityService.StartTracking(Pair.Ident);
            return;
        }

        _deferred = Guid.Empty;

        SetUploading(isUploading: false);

        if (Pair.IsDownloadBlocked)
        {
            var reasons = string.Join(", ", Pair.HoldDownloadReasons);
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                $"Not applying character data: {reasons}")));
            Logger.LogDebug("[BASE-{appBase}] Not applying due to hold: {reasons}", applicationBase, reasons);
            var hasDiffMods = characterData.CheckUpdatedData(applicationBase, _cachedData, Logger,
                this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _forceApplyMods = hasDiffMods || _forceApplyMods || (PlayerCharacter == IntPtr.Zero && _cachedData == null);
            _cachedData = characterData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, characterData));
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase, _cachedData.DataHash.Value, _forceApplyMods);
            return;
        }

        Logger.LogDebug("[BASE-{appbase}] Applying data for {player}, forceApplyCustomization: {forced}, forceApplyMods: {forceMods}", applicationBase, this, forceApplyCustomization, _forceApplyMods);
        Logger.LogDebug("[BASE-{appbase}] Hash for data is {newHash}, current cache hash is {oldHash}", applicationBase, characterData.DataHash.Value, _cachedData?.DataHash.Value ?? "NODATA");

        if (string.Equals(characterData.DataHash.Value, _cachedData?.DataHash.Value ?? string.Empty, StringComparison.Ordinal) && !forceApplyCustomization) return;

        if (_dalamudUtil.IsInCutscene || _dalamudUtil.IsInGpose || !_ipcManager.Penumbra.APIAvailable || !_ipcManager.Glamourer.APIAvailable)
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: you are in GPose, a Cutscene or Penumbra/Glamourer is not available")));
            Logger.LogInformation("[BASE-{appbase}] Application of data for {player} while in cutscene/gpose or Penumbra/Glamourer unavailable, returning", applicationBase, this);
            return;
        }

        Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
            "Applying Character Data")));

        _forceApplyMods |= forceApplyCustomization;

        var charaDataToUpdate = characterData.CheckUpdatedData(applicationBase, _cachedData?.DeepClone() ?? new(), Logger, this, forceApplyCustomization, _forceApplyMods);

        if (_charaHandler != null && _forceApplyMods)
        {
            _forceApplyMods = false;
        }

        if (_redrawOnNextApplication && charaDataToUpdate.TryGetValue(ObjectKind.Player, out var player))
        {
            player.Add(PlayerChanges.ForcedRedraw);
            _redrawOnNextApplication = false;
        }

        if (charaDataToUpdate.TryGetValue(ObjectKind.Player, out var playerChanges))
        {
            _pluginWarningNotificationManager.NotifyForMissingPlugins(Pair.UserData, PlayerName!, playerChanges);
        }

        Logger.LogDebug("[BASE-{appbase}] Downloading and applying character for {name}", applicationBase, this);

        DownloadAndApplyCharacter(applicationBase, characterData.DeepClone(), charaDataToUpdate);
    }

    public override string ToString()
    {
        return Pair == null
            ? base.ToString() ?? string.Empty
            : Pair.UserData.AliasOrUID + ":" + PlayerName + ":" + (PlayerCharacter != nint.Zero ? "HasChar" : "NoChar");
    }

    internal void SetUploading(bool isUploading = true)
    {
        Logger.LogTrace("Setting {this} uploading {uploading}", this, isUploading);
        if (_charaHandler != null)
        {
            Mediator.Publish(new PlayerUploadingMessage(_charaHandler, isUploading));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        _visibilityService.StopTracking(Pair.Ident);

        SetUploading(isUploading: false);
        var name = PlayerName;
        Logger.LogDebug("Disposing {name} ({user})", name, Pair);
        try
        {
            Guid applicationId = Guid.NewGuid();

            if (!string.IsNullOrEmpty(name))
            {
                Mediator.Publish(new EventMessage(new Event(name, Pair.UserData, nameof(PairHandler), EventSeverity.Informational, "Disposing User")));
            }

            UndoApplicationAsync(applicationId).GetAwaiter().GetResult();

            _applicationCancellationTokenSource?.Dispose();
            _applicationCancellationTokenSource = null;
            _downloadCancellationTokenSource?.Dispose();
            _downloadCancellationTokenSource = null;
            _charaHandler?.Dispose();
            _charaHandler = null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error on disposal of {name}", name);
        }
        finally
        {
            PlayerName = null;
            _cachedData = null;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, null));
            Logger.LogDebug("Disposing {name} complete", name);
        }
    }

    public void UndoApplication(Guid applicationId = default)
    {
        _ = Task.Run(async () => {
            await UndoApplicationAsync(applicationId).ConfigureAwait(false);
        });
    }

    private async Task UndoApplicationAsync(Guid applicationId = default)
    {
        Logger.LogDebug($"Undoing application of {Pair.UserPair}");
        var name = PlayerName;
        try
        {
            if (applicationId == default)
                applicationId = Guid.NewGuid();
            _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate();
            _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate();

            Logger.LogDebug("[{applicationId}] Removing Temp Collection for {name} ({user})", applicationId, name, Pair.UserPair);
            if (_penumbraCollection != Guid.Empty)
            {
                await _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(Logger, applicationId, _penumbraCollection).ConfigureAwait(false);
                _penumbraCollection = Guid.Empty;
            }

            if (_dalamudUtil is { IsZoning: false, IsInCutscene: false } && !string.IsNullOrEmpty(name))
            {
                Logger.LogTrace("[{applicationId}] Restoring state for {name} ({OnlineUser})", applicationId, name, Pair.UserPair);
                if (!IsVisible)
                {
                    Logger.LogDebug("[{applicationId}] Restoring Glamourer for {name} ({user})", applicationId, name, Pair.UserPair);
                    await _ipcManager.Glamourer.RevertByNameAsync(Logger, name, applicationId).ConfigureAwait(false);
                }
                else
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(TimeSpan.FromSeconds(60));

                    Logger.LogInformation("[{applicationId}] CachedData is null {isNull}, contains things: {contains}", applicationId, _cachedData == null, _cachedData?.FileReplacements.Any() ?? false);

                    foreach (KeyValuePair<ObjectKind, List<FileReplacementData>> item in _cachedData?.FileReplacements ?? [])
                    {
                        try
                        {
                            await RevertCustomizationDataAsync(item.Key, name, applicationId, cts.Token).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            Logger.LogWarning(ex, "Failed disposing player (not present anymore?)");
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error on undoing application of {name}", name);
        }
    }

    private async Task ApplyCustomizationDataAsync(Guid applicationId, KeyValuePair<ObjectKind, HashSet<PlayerChanges>> changes, CharacterData charaData, CancellationToken token)
        {
        if (PlayerCharacter == nint.Zero) return;
        var ptr = PlayerCharacter;

        var handler = changes.Key switch
        {
            ObjectKind.Player => _charaHandler!,
            ObjectKind.Companion => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetCompanion(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.MinionOrMount => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetMinionOrMount(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.Pet => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetPet(ptr), isWatched: false).ConfigureAwait(false),
            _ => throw new NotSupportedException("ObjectKind not supported: " + changes.Key)
        };

        try
        {
            if (handler.Address == nint.Zero)
            {
                return;
            }

            Logger.LogDebug("[{applicationId}] Applying Customization Data for {handler}", applicationId, handler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, handler, applicationId, 30000, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var change in changes.Value.OrderBy(p => (int)p))
            {
                Logger.LogDebug("[{applicationId}] Processing {change} for {handler}", applicationId, change, handler);
                switch (change)
                {
                    case PlayerChanges.Customize:
                        if (charaData.CustomizePlusData.TryGetValue(changes.Key, out var customizePlusData))
                        {
                            _customizeIds[changes.Key] = await _ipcManager.CustomizePlus.SetBodyScaleAsync(handler.Address, customizePlusData).ConfigureAwait(false);
                        }
                        else if (_customizeIds.TryGetValue(changes.Key, out var customizeId))
                        {
                            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                            _customizeIds.Remove(changes.Key);
                        }
                        break;

                    case PlayerChanges.Heels:
                        await _ipcManager.Heels.SetOffsetForPlayerAsync(handler.Address, charaData.HeelsData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.Honorific:
                        await _ipcManager.Honorific.SetTitleAsync(handler.Address, charaData.HonorificData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.Glamourer:
                        if (charaData.GlamourerData.TryGetValue(changes.Key, out var glamourerData))
                        {
                            await _ipcManager.Glamourer.ApplyAllAsync(Logger, handler, glamourerData, applicationId, token).ConfigureAwait(false);
                        }
                        break;

                    case PlayerChanges.Moodles:
                        await _ipcManager.Moodles.SetStatusAsync(handler.Address, charaData.MoodlesData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.PetNames:
                        await _ipcManager.PetNames.SetPlayerData(handler.Address, charaData.PetNamesData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.ForcedRedraw:
                        await _ipcManager.Penumbra.RedrawAsync(Logger, handler, applicationId, token).ConfigureAwait(false);
                        break;

                    default:
                        break;
                }
                token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (handler != _charaHandler) handler.Dispose();
        }
    }

    private void DownloadAndApplyCharacter(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData)
    {
        if (!updatedData.Any())
        {
            Logger.LogDebug("[BASE-{appBase}] Nothing to update for {obj}", applicationBase, this);
            return;
        }

        var updateModdedPaths = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModFiles));
        var updateManip = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModManip));

        _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate() ?? new CancellationTokenSource();
        var downloadToken = _downloadCancellationTokenSource.Token;

        _ = DownloadAndApplyCharacterAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, downloadToken).ConfigureAwait(false);
    }

    private Task? _pairDownloadTask;

    private async Task DownloadAndApplyCharacterAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData,
        bool updateModdedPaths, bool updateManip, CancellationToken downloadToken)
    {
        Dictionary<(string GamePath, string? Hash), string> moddedPaths = [];

        if (updateModdedPaths)
        {
            int attempts = 0;
            List<FileReplacementData> toDownloadReplacements = TryCalculateModdedDictionary(applicationBase, charaData, out moddedPaths, downloadToken);

            while (toDownloadReplacements.Count > 0 && attempts++ <= 10 && !downloadToken.IsCancellationRequested)
            {
                if (_pairDownloadTask != null && !_pairDownloadTask.IsCompleted)
                {
                    Logger.LogDebug("[BASE-{appBase}] Finishing prior running download task for player {name}, {kind}", applicationBase, PlayerName, updatedData);
                    await _pairDownloadTask.ConfigureAwait(false);
                }

                Logger.LogDebug("[BASE-{appBase}] Downloading missing files for player {name}, {kind}", applicationBase, PlayerName, updatedData);

                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
                    $"Starting download for {toDownloadReplacements.Count} files")));
                var toDownloadFiles = await _downloadManager.InitiateDownloadList(_charaHandler!, toDownloadReplacements, downloadToken).ConfigureAwait(false);

                if (!_playerPerformanceService.ComputeAndAutoPauseOnVRAMUsageThresholds(this, charaData, toDownloadFiles))
                {
                    _downloadManager.ClearDownload();
                    return;
                }

                _pairDownloadTask = Task.Run(async () => await _downloadManager.DownloadFiles(_charaHandler!, toDownloadReplacements, downloadToken).ConfigureAwait(false));

                await _pairDownloadTask.ConfigureAwait(false);

                if (downloadToken.IsCancellationRequested)
                {
                    Logger.LogTrace("[BASE-{appBase}] Detected cancellation", applicationBase);
                    return;
                }

                toDownloadReplacements = TryCalculateModdedDictionary(applicationBase, charaData, out moddedPaths, downloadToken);

                if (toDownloadReplacements.TrueForAll(c => _downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, c.Hash, StringComparison.Ordinal))))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), downloadToken).ConfigureAwait(false);
            }

            if (!await _playerPerformanceService.CheckBothThresholds(this, charaData).ConfigureAwait(false))
                return;
        }

        downloadToken.ThrowIfCancellationRequested();

        var appToken = _applicationCancellationTokenSource?.Token;
        while ((!_applicationTask?.IsCompleted ?? false)
               && !downloadToken.IsCancellationRequested
               && (!appToken?.IsCancellationRequested ?? false))
        {
            // block until current application is done
            Logger.LogDebug("[BASE-{appBase}] Waiting for current data application (Id: {id}) for player ({handler}) to finish", applicationBase, _applicationId, PlayerName);
            await Task.Delay(250).ConfigureAwait(false);
        }

        if (downloadToken.IsCancellationRequested || (appToken?.IsCancellationRequested ?? false)) return;

        _applicationCancellationTokenSource = _applicationCancellationTokenSource.CancelRecreate() ?? new CancellationTokenSource();
        var token = _applicationCancellationTokenSource.Token;

        _applicationTask = ApplyCharacterDataAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, moddedPaths, token);
    }

    private async Task ApplyCharacterDataAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData, bool updateModdedPaths, bool updateManip,
        Dictionary<(string GamePath, string? Hash), string> moddedPaths, CancellationToken token)
    {
        ushort objIndex = ushort.MaxValue;
        try
        {
            _applicationId = Guid.NewGuid();
            Logger.LogDebug("[BASE-{applicationId}] Starting application task for {this}: {appId}", applicationBase, this, _applicationId);

            if (_penumbraCollection == Guid.Empty)
            {
                if (objIndex == ushort.MaxValue)
                    objIndex = await _dalamudUtil.RunOnFrameworkThread(() => _charaHandler!.GetGameObject()!.ObjectIndex).ConfigureAwait(false);
                _penumbraCollection = await _ipcManager.Penumbra.CreateTemporaryCollectionAsync(Logger, Pair.UserData.UID).ConfigureAwait(false);
                await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, objIndex).ConfigureAwait(false);
            }

            Logger.LogDebug("[{applicationId}] Waiting for initial draw for for {handler}", _applicationId, _charaHandler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, _charaHandler!, _applicationId, 30000, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (updateModdedPaths)
            {
                // ensure collection is set
                if (objIndex == ushort.MaxValue)
                    objIndex = await _dalamudUtil.RunOnFrameworkThread(() => _charaHandler!.GetGameObject()!.ObjectIndex).ConfigureAwait(false);
                await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, objIndex).ConfigureAwait(false);

                await _ipcManager.Penumbra.SetTemporaryModsAsync(Logger, _applicationId, _penumbraCollection,
                    moddedPaths.ToDictionary(k => k.Key.GamePath, k => k.Value, StringComparer.Ordinal)).ConfigureAwait(false);
                LastAppliedDataBytes = -1;
                foreach (var path in moddedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(v => new FileInfo(v)).Where(p => p.Exists))
                {
                    if (LastAppliedDataBytes == -1) LastAppliedDataBytes = 0;

                    LastAppliedDataBytes += path.Length;
                }
            }

            if (updateManip)
            {
                await _ipcManager.Penumbra.SetManipulationDataAsync(Logger, _applicationId, _penumbraCollection, charaData.ManipulationData).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            foreach (var kind in updatedData)
            {
                await ApplyCustomizationDataAsync(_applicationId, kind, charaData, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            _cachedData = charaData;
            Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));

            Logger.LogDebug("[{applicationId}] Application finished", _applicationId);
        }
        catch (Exception ex)
        {
            if (ex is AggregateException aggr && aggr.InnerExceptions.Any(e => e is ArgumentNullException))
            {
                IsVisible = false;
                _forceApplyMods = true;
                _cachedData = charaData;
                Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, charaData));
                Logger.LogDebug("[{applicationId}] Cancelled, player turned null during application", _applicationId);
            }
            else
            {
                Logger.LogWarning(ex, "[{applicationId}] Cancelled", _applicationId);
            }
        }
    }

    private void UpdateVisibility(bool nowVisible, bool invalidate = false)
    {
        if (string.IsNullOrEmpty(PlayerName))
        {
            var pc = _dalamudUtil.FindPlayerByNameHash(Pair.Ident);
            if (pc.ObjectId == 0) return;
            Logger.LogDebug("One-Time Initializing {this}", this);
            Initialize(pc.Name);
            Logger.LogDebug("One-Time Initialized {this}", this);
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
                $"Initializing User For Character {pc.Name}")));
        }

        // This was triggered by the character becoming handled by Mare, so unapply everything
        // There seems to be a good chance that this races Mare and then crashes
        if (!nowVisible && invalidate)
        {
            bool wasVisible = IsVisible;
            IsVisible = false;
            _charaHandler?.Invalidate();
            _downloadCancellationTokenSource?.CancelDispose();
            _downloadCancellationTokenSource = null;
            if (wasVisible)
                Logger.LogTrace("{this} visibility changed, now: {visi}", this, IsVisible);
            Logger.LogDebug("Invalidating {this}", this);
            UndoApplication();
            return;
        }

        if (!IsVisible && nowVisible)
        {
            // This is deferred application attempt, avoid any log output
            if (_deferred != Guid.Empty)
            {
                _isVisible = true;
                _ = Task.Run(() =>
                {
                    ApplyCharacterData(_deferred, _cachedData!, forceApplyCustomization: true);
                });
            }

            IsVisible = true;
            Mediator.Publish(new PairHandlerVisibleMessage(this));
            if (_cachedData != null)
            {
                Guid appData = Guid.NewGuid();
                Logger.LogTrace("[BASE-{appBase}] {this} visibility changed, now: {visi}, cached data exists", appData, this, IsVisible);

                _ = Task.Run(() =>
                {
                    ApplyCharacterData(appData, _cachedData!, forceApplyCustomization: true);
                });
            }
            else
            {
                Logger.LogTrace("{this} visibility changed, now: {visi}, no cached data exists", this, IsVisible);
            }
        }
        else if (IsVisible && !nowVisible)
        {
            IsVisible = false;
            _charaHandler?.Invalidate();
            _downloadCancellationTokenSource?.CancelDispose();
            _downloadCancellationTokenSource = null;
            Logger.LogTrace("{this} visibility changed, now: {visi}", this, IsVisible);
        }
    }

    private void Initialize(string name)
    {
        PlayerName = name;
        _charaHandler = _gameObjectHandlerFactory.Create(ObjectKind.Player, () => _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident), isWatched: false).GetAwaiter().GetResult();

        _serverConfigManager.AutofillNoteWithCharacterName(Pair.UserData.UID, name);

        Mediator.Subscribe<HonorificReadyMessage>(this, msg =>
        {
            if (string.IsNullOrEmpty(_cachedData?.HonorificData)) return;
            Logger.LogTrace("Reapplying Honorific data for {this}", this);
            _ = Task.Run(async () => await _ipcManager.Honorific.SetTitleAsync(PlayerCharacter, _cachedData.HonorificData).ConfigureAwait(false), CancellationToken.None);
        });

        Mediator.Subscribe<PetNamesReadyMessage>(this, msg =>
        {
            if (string.IsNullOrEmpty(_cachedData?.PetNamesData)) return;
            Logger.LogTrace("Reapplying Pet Names data for {this}", this);
            _ = Task.Run(async () => await _ipcManager.PetNames.SetPlayerData(PlayerCharacter, _cachedData.PetNamesData).ConfigureAwait(false), CancellationToken.None);
        });
    }

    private async Task RevertCustomizationDataAsync(ObjectKind objectKind, string name, Guid applicationId, CancellationToken cancelToken)
    {
        nint address = _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident);
        if (address == nint.Zero) return;

        Logger.LogDebug("[{applicationId}] Reverting all Customization for {alias}/{name} {objectKind}", applicationId, Pair.UserData.AliasOrUID, name, objectKind);

        if (_customizeIds.TryGetValue(objectKind, out var customizeId))
        {
            _customizeIds.Remove(objectKind);
        }

        if (objectKind == ObjectKind.Player)
        {
            using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => address, isWatched: false).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Customization and Equipment for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Heels for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Heels.RestoreOffsetForPlayerAsync(address).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring C+ for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Honorific for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Honorific.ClearTitleAsync(address).ConfigureAwait(false);
            Logger.LogDebug("[{applicationId}] Restoring Pet Nicknames for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.PetNames.ClearPlayerData(address).ConfigureAwait(false);
            Logger.LogDebug("[{applicationId}] Restoring Moodles for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Moodles.RevertStatusAsync(address).ConfigureAwait(false);
        }
        else if (objectKind == ObjectKind.MinionOrMount)
        {
            var minionOrMount = await _dalamudUtil.GetMinionOrMountAsync(address).ConfigureAwait(false);
            if (minionOrMount != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => minionOrMount, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Pet)
        {
            var pet = await _dalamudUtil.GetPetAsync(address).ConfigureAwait(false);
            if (pet != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => pet, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Companion)
        {
            var companion = await _dalamudUtil.GetCompanionAsync(address).ConfigureAwait(false);
            if (companion != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => companion, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
    }

    private List<FileReplacementData> TryCalculateModdedDictionary(Guid applicationBase, CharacterData charaData, out Dictionary<(string GamePath, string? Hash), string> moddedDictionary, CancellationToken token)
    {
        Stopwatch st = Stopwatch.StartNew();
        ConcurrentBag<FileReplacementData> missingFiles = [];
        moddedDictionary = [];
        ConcurrentDictionary<(string GamePath, string? Hash), string> outputDict = new();
        bool hasMigrationChanges = false;

        try
        {
            var replacementList = charaData.FileReplacements.SelectMany(k => k.Value.Where(v => string.IsNullOrEmpty(v.FileSwapPath))).ToList();
            Parallel.ForEach(replacementList, new ParallelOptions()
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = 4
            },
            (item) =>
            {
                token.ThrowIfCancellationRequested();
                var fileCache = _fileDbManager.GetFileCacheByHash(item.Hash, preferSubst: true);
                if (fileCache != null)
                {
                    if (string.IsNullOrEmpty(new FileInfo(fileCache.ResolvedFilepath).Extension))
                    {
                        hasMigrationChanges = true;
                        fileCache = _fileDbManager.MigrateFileHashToExtension(fileCache, item.GamePaths[0].Split(".")[^1]);
                    }
                    _databaseService.RecordFileSeen(Pair.UserData.UID, item.Hash, DateTime.UtcNow);
                    
                    foreach (var gamePath in item.GamePaths)
                    {
                        outputDict[(gamePath, item.Hash)] = fileCache.ResolvedFilepath;
                    }
                }
                else
                {
                    Logger.LogTrace("Missing file: {hash}", item.Hash);
                    missingFiles.Add(item);
                }
            });

            moddedDictionary = outputDict.ToDictionary(k => k.Key, k => k.Value);

            foreach (var item in charaData.FileReplacements.SelectMany(k => k.Value.Where(v => !string.IsNullOrEmpty(v.FileSwapPath))).ToList())
            {
                foreach (var gamePath in item.GamePaths)
                {
                    Logger.LogTrace("[BASE-{appBase}] Adding file swap for {path}: {fileSwap}", applicationBase, gamePath, item.FileSwapPath);
                    moddedDictionary[(gamePath, null)] = item.FileSwapPath;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BASE-{appBase}] Something went wrong during calculation replacements", applicationBase);
        }
        if (hasMigrationChanges) _fileDbManager.WriteOutFullCsv();
        st.Stop();
        Logger.LogDebug("[BASE-{appBase}] ModdedPaths calculated in {time}ms, missing files: {count}, total files: {total}", applicationBase, st.ElapsedMilliseconds, missingFiles.Count, moddedDictionary.Keys.Count);
        return [.. missingFiles];
    }
}