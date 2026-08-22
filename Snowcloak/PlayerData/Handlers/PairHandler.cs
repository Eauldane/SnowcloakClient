using ElezenTools.Services;
using Snowcloak.API.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ElezenTools.Core.Async;
using Snowcloak.FileCache;
using Snowcloak.Interop.Ipc;
using Snowcloak.Ipc;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Data;
using Snowcloak.PlayerData.Factories;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Core.PlayerData;
using Snowcloak.Services;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ModNullification;
using Snowcloak.Services.Performance;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using Snowcloak.WebAPI.Files;
using System.Collections.Concurrent;
using System.Diagnostics;
using CharacterData = Snowcloak.API.Data.CharacterData;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Handlers;

public sealed partial class PairHandler : DisposableMediatorSubscriberBase, IAsyncDisposable, IApplyTarget
{
    private sealed record CombatData(Guid ApplicationId, CharacterData CharacterData, bool Forced);

    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileDownloadManager _downloadManager;
    private readonly FileCacheManager _fileDbManager;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly IpcManager _ipcManager;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly NotesStore _notesStore;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager;
    private readonly VisibilityService _visibilityService;
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly SingleFlightCts _applicationFlight = new();
    private Guid _applicationId;
    private Task? _applicationTask;
    private readonly SemaphoreSlim _applicationGate = new(1, 1);
    private readonly PairAppliedState _appliedState = new();
    private GameObjectHandler? _charaHandler;
    private CombatData? _dataReceivedInDowntime;
    private readonly SingleFlightCts _downloadFlight = new();
    private bool _isVisible;
    private Guid _deferred = Guid.Empty;
    private Guid _penumbraCollection = Guid.Empty;
    private bool _applyAttemptedWhileReady;
    private readonly DatabaseService _databaseService;
    private readonly CharacterReverter _reverter;
    private readonly CharacterApplicationPipeline _pipeline;
    private readonly PairVisibilityTracker _tracker;
    private readonly IDisposable _retryRegistration;
    private Task? _pairDownloadTask;
    private int _modRecoveryRetryAttempt;
    private int _modRecoveryRetryScheduled;
    private int _requiredIpcRecoveryScheduled;
    private int _disposed;

    public PairHandler(ILogger<PairHandler> logger, Pair pair, PairAnalyzer pairAnalyzer,
        GameObjectHandlerFactory gameObjectHandlerFactory,
        IpcManager ipcManager, FileDownloadManager transferManager,
        PluginWarningNotificationService pluginWarningNotificationManager,
        DalamudUtilService dalamudUtil, IHostApplicationLifetime lifetime,
        FileCacheManager fileDbManager, SnowMediator mediator,
        PlayerPerformanceService playerPerformanceService,
        NotesStore notesStore,
        SnowcloakConfigService configService, VisibilityService visibilityService, DatabaseService databaseService,
        ModNullificationService modNullificationService, UsageStatisticsService usageStatisticsService,
        ApplicationAdmissionController applicationAdmissionController,
        DeferredApplicationRetryCoordinator retryCoordinator) : base(logger, mediator)
    {
        ArgumentNullException.ThrowIfNull(retryCoordinator);

        Pair = pair;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        PairAnalyzer = pairAnalyzer;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _ipcManager = ipcManager;
        _downloadManager = transferManager;
        _pluginWarningNotificationManager = pluginWarningNotificationManager;
        _dalamudUtil = dalamudUtil;
        _fileDbManager = fileDbManager;
        _playerPerformanceService = playerPerformanceService;
        _notesStore = notesStore;
        _configService = configService;
        _visibilityService = visibilityService;
        _databaseService = databaseService;

        _reverter = new CharacterReverter(this, Logger, Pair, _appliedState, _ipcManager, _dalamudUtil,
            _gameObjectHandlerFactory, _backgroundTasks, _runtimeCts, _applicationFlight, _downloadFlight);
        _pipeline = new CharacterApplicationPipeline(this, Logger, Mediator, Pair, _appliedState, _backgroundTasks,
            _runtimeCts, _applicationFlight, _downloadFlight, _applicationGate, _downloadManager, _playerPerformanceService,
            _ipcManager, new ApplyGameState(_dalamudUtil), _fileDbManager, _databaseService, _gameObjectHandlerFactory, modNullificationService,
            usageStatisticsService, applicationAdmissionController);
        _tracker = new PairVisibilityTracker(this, Logger, Mediator, Pair, _appliedState, _backgroundTasks, _downloadFlight,
            _visibilityService, _dalamudUtil, _gameObjectHandlerFactory, _notesStore, _ipcManager);

        _visibilityService.StartTracking(Pair.Ident);

        Mediator.SubscribeKeyed<PlayerVisibilityMessage>(this, Pair.Ident, (msg) => _tracker.UpdateVisibility(msg.IsVisible, msg.Invalidate));

        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (_) =>
        {
            _downloadFlight.Cancel();
            _reverter.QueueClearPlayerScopedOptionalData(Guid.NewGuid());
            _charaHandler?.Invalidate();
            RearmVisibilityTracking();
            IsVisible = false;
        });
        Mediator.Subscribe<PenumbraInitializedMessage>(this, (_) =>
        {
            _penumbraCollection = Guid.Empty;
            if (_appliedState.CachedData != null || Pair.LastReceivedCharacterData != null)
            {
                _appliedState.RequireModRecovery();
            }
            if (!IsVisible && _charaHandler != null)
            {
                PlayerName = string.Empty;
                _charaHandler.Dispose();
                _charaHandler = null;
            }
            ScheduleRequiredIpcRecovery();
        });
        Mediator.Subscribe<RequiredIpcAvailabilityChangedMessage>(this, message =>
        {
            if (message.IsAvailable)
            {
                ScheduleRequiredIpcRecovery();
            }
        });
        Mediator.Subscribe<ClassJobChangedMessage>(this, (msg) =>
        {
            if (msg.GameObjectHandler == _charaHandler)
            {
                _appliedState.RedrawOnNextApplication = true;
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
            if (ShouldHoldApplicationForCombatOrPerformance())
            {
                _dataReceivedInDowntime = null;
                _downloadFlight.Cancel();
                _applicationFlight.Cancel();
            }
        });
        Mediator.Subscribe<RecalculatePerformanceMessage>(this, (msg) =>
        {
            if (msg.UID != null
                && !msg.UID.Equals(Pair.UserData.UID, StringComparison.Ordinal)
                && !msg.UID.Equals(Pair.UserData.Alias, StringComparison.Ordinal)) return;
            LogRecalculatingPerformance(Logger, Pair.UserData.UID);
            _playerPerformanceService.ReevaluateAutoPause(this);
            pair.ApplyLastReceivedData(forced: true);
        });

        _retryRegistration = retryCoordinator.Register(_tracker.RetryDeferredApplicationIfReady);

        LastAppliedDataBytes = -1;
    }

    public bool IsVisible
    {
        get => _isVisible;
        internal set
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

    public long LastAppliedDataBytes { get; internal set; }
    public Pair Pair { get; private init; }
    public PairAnalyzer PairAnalyzer { get; private init; }
    public nint PlayerCharacter => _charaHandler?.Address ?? nint.Zero;
    public ushort? ObjectIndex => _charaHandler?.ObjectIndex;
    public unsafe uint PlayerCharacterId => (_charaHandler?.Address ?? nint.Zero) == nint.Zero
        ? uint.MaxValue
        : ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_charaHandler!.Address)->EntityId;
    public string? PlayerName { get; internal set; }
    public string PlayerNameHash => Pair.Ident;

    internal GameObjectHandler? CharaHandler { get => _charaHandler; set => _charaHandler = value; }
    public Guid PenumbraCollection { get => _penumbraCollection; set => _penumbraCollection = value; }
    internal Guid Deferred { get => _deferred; set => _deferred = value; }
    internal bool ApplyAttemptedWhileReady { get => _applyAttemptedWhileReady; set => _applyAttemptedWhileReady = value; }
    internal Task? ApplicationTask { get => _applicationTask; set => _applicationTask = value; }
    internal Task? PairDownloadTask { get => _pairDownloadTask; set => _pairDownloadTask = value; }
    internal Guid ApplicationId { get => _applicationId; set => _applicationId = value; }
    internal CharacterReverter Reverter => _reverter;

    internal void RearmVisibilityTracking() => _visibilityService.RearmTracking(Pair.Ident);

    internal void ResetModRecoveryRetry()
        => Volatile.Write(ref _modRecoveryRetryAttempt, 0);

    internal void ScheduleModRecoveryRetry()
    {
        if (Interlocked.Exchange(ref _modRecoveryRetryScheduled, 1) != 0)
        {
            return;
        }

        var attempt = Interlocked.Increment(ref _modRecoveryRetryAttempt);
        if (attempt > 5)
        {
            Volatile.Write(ref _modRecoveryRetryScheduled, 0);
            return;
        }

        var delay = attempt switch
        {
            1 => TimeSpan.FromMilliseconds(500),
            2 => TimeSpan.FromSeconds(1),
            3 => TimeSpan.FromSeconds(2),
            4 => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(10),
        };

        _ = _backgroundTasks.Run(async () =>
        {
            var ownsScheduledFlag = true;
            try
            {
                await Task.Delay(delay, _runtimeCts.Token).ConfigureAwait(false);
                if (_appliedState.ForceApplyMods
                    && _ipcManager.Initialized
                    && IsVisible
                    && Pair.LastReceivedCharacterData != null)
                {
                    Volatile.Write(ref _modRecoveryRetryScheduled, 0);
                    ownsScheduledFlag = false;
                    Pair.ApplyLastReceivedData(forced: true);
                }
            }
            catch (OperationCanceledException) when (_runtimeCts.IsCancellationRequested)
            {
            }
            finally
            {
                if (ownsScheduledFlag)
                {
                    Volatile.Write(ref _modRecoveryRetryScheduled, 0);
                }
            }
        }, nameof(ScheduleModRecoveryRetry));
    }

    public void UndoApplication(Guid applicationId = default)
    {
        _reverter.UndoApplication(applicationId);
        Mediator.Publish(new PairApplicationStateChangedMessage(Pair.UserData.UID, SnowcloakApplicationState.Idle));
    }

    public void ApplyCharacterData(Guid applicationBase, CharacterData characterData, bool forceApplyCustomization = false, PairFilterContext? filter = null)
    {
        ArgumentNullException.ThrowIfNull(characterData);

        using var baseLogScope = Logger.BeginScope("BASE-{ApplicationBase}", applicationBase);
        if (filter.HasValue)
        {
            characterData = _pipeline.FilterReceivedData(characterData, filter.Value);
        }

        if (ShouldHoldApplicationForCombatOrPerformance())
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: you are in combat or performing music, deferring application")));
            LogReceivedInCombat(Logger);
            _dataReceivedInDowntime = new(applicationBase, characterData, forceApplyCustomization);
            SetUploading(isUploading: false);
            Mediator.Publish(new PairApplicationStateChangedMessage(Pair.UserData.UID, SnowcloakApplicationState.Blocked, "Application is deferred during combat or performance."));
            return;
        }

        if (_charaHandler == null || (PlayerCharacter == IntPtr.Zero))
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: Receiving Player is in an invalid state, deferring application")));
            LogReceivedInvalidState(Logger, _charaHandler == null, PlayerCharacter == IntPtr.Zero);
            CacheDataForDeferredApplication(characterData);
            // Defer: RetryDeferredApplicationIfReady (framework tick) re-attempts once the
            // character reaches a valid/ready state, instead of bouncing the visibility service.
            _deferred = applicationBase;
            _applyAttemptedWhileReady = false;
            Mediator.Publish(new PairApplicationStateChangedMessage(Pair.UserData.UID, SnowcloakApplicationState.Waiting, "Waiting for the character to become ready."));
            return;
        }

        _deferred = Guid.Empty;
        _appliedState.LastKnownPlayerAddress = _charaHandler.Address;

        SetUploading(isUploading: false);

        _playerPerformanceService.CheckReportedThresholds(this, Pair.LastReportedTriangles, Pair.LastReportedApproximateVRAMBytes);

        if (Pair.IsDownloadBlocked)
        {
            var reasons = string.Join(", ", Pair.HoldDownloadReasons);
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                $"Not applying character data: {reasons}")));
            LogNotApplyingDueToHold(Logger, reasons);
            CacheDataForDeferredApplication(characterData);
            Mediator.Publish(new PairApplicationStateChangedMessage(Pair.UserData.UID, SnowcloakApplicationState.Blocked, reasons));
            return;
        }

        var modRecoveryGeneration = _appliedState.CaptureModRecoveryGeneration();
        var forceApplyMods = modRecoveryGeneration != 0;

        LogApplyingData(Logger, this, forceApplyCustomization, forceApplyMods);
        LogHashForData(Logger, characterData.DataHash.Value, _appliedState.CachedData?.DataHash.Value ?? "NODATA");

        if (string.Equals(characterData.DataHash.Value, _appliedState.CachedData?.DataHash.Value ?? string.Empty, StringComparison.Ordinal)
            && !forceApplyCustomization
            && !forceApplyMods)
        {
            Mediator.Publish(new PairApplicationStateChangedMessage(Pair.UserData.UID, SnowcloakApplicationState.Applied));
            return;
        }

        if (_dalamudUtil.IsInCutscene || _dalamudUtil.IsInGpose || !_ipcManager.Penumbra.APIAvailable || !_ipcManager.Glamourer.APIAvailable)
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: you are in GPose, a Cutscene or Penumbra/Glamourer is not available")));
            LogApplyUnavailable(Logger, this);
            Mediator.Publish(new PairApplicationStateChangedMessage(Pair.UserData.UID, SnowcloakApplicationState.Blocked, "Application is unavailable in the current game or plugin state."));
            return;
        }

        Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
            "Applying Character Data")));

        var charaDataToUpdate = CharacterDataDiffer.Diff(_appliedState.CachedData, characterData);
        CharacterApplicationPlanner.ApplyForceModifiers(charaDataToUpdate, _appliedState.CachedData, characterData,
            forceApplyCustomization, forceApplyMods);

        if (_appliedState.RedrawOnNextApplication && charaDataToUpdate.TryGetValue(ObjectKind.Player, out _))
        {
            charaDataToUpdate.Add(ObjectKind.Player, PlayerChanges.ForcedRedraw);
            _appliedState.RedrawOnNextApplication = false;
        }

        LogCharacterDataChanges(charaDataToUpdate);

        if (charaDataToUpdate.TryGetValue(ObjectKind.Player, out var playerChanges))
        {
            _pluginWarningNotificationManager.NotifyForMissingPlugins(Pair.UserData, PlayerName!, playerChanges);
        }

        var unavailableOptionalStatuses = _ipcManager.GetUnavailableOptionalStatusesForChanges(charaDataToUpdate);
        if (unavailableOptionalStatuses.Count > 0)
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Applying character data without optional IPC: " + string.Join(", ", unavailableOptionalStatuses.Select(DescribeSkippedOptionalIpc)))));
        }

        LogDownloadingAndApplying(Logger, this);

        _pipeline.DownloadAndApplyCharacter(characterData.Clone(), charaDataToUpdate, modRecoveryGeneration);
    }

    private bool ShouldHoldApplicationForCombatOrPerformance()
        => ApplicationHoldPolicy.ShouldHoldForCombatOrPerformance(_configService.Current.HoldCombatApplication,
            _dalamudUtil.IsInCombatOrPerforming);

    private void ScheduleRequiredIpcRecovery()
    {
        if (Interlocked.Exchange(ref _requiredIpcRecoveryScheduled, 1) != 0)
        {
            return;
        }

        _ = _backgroundTasks.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), _runtimeCts.Token).ConfigureAwait(false);
                if (_ipcManager.Initialized && IsVisible && Pair.LastReceivedCharacterData != null)
                {
                    Pair.ApplyLastReceivedData(forced: true);
                }
            }
            catch (OperationCanceledException) when (_runtimeCts.IsCancellationRequested)
            {
                // Runtime teardown owns cancellation.
            }
            finally
            {
                Volatile.Write(ref _requiredIpcRecoveryScheduled, 0);
            }
        }, nameof(ScheduleRequiredIpcRecovery));
    }

    // Shared by the "invalid state" and "download blocked" defer paths: recompute the force-mods
    // flag, cache the latest data, and notify. Does not itself apply anything.
    private void CacheDataForDeferredApplication(CharacterData characterData)
    {
        var hasDiffMods = CharacterDataDiffer.Diff(_appliedState.CachedData, characterData)
            .ContainsAny(PlayerChanges.ModManip, PlayerChanges.ModFiles);
        if (hasDiffMods || _appliedState.ForceApplyMods || (PlayerCharacter == IntPtr.Zero && _appliedState.CachedData == null))
        {
            _appliedState.RequireModRecovery();
        }
        _appliedState.CachedData = characterData;
        Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, characterData));
        LogSettingData(Logger, _appliedState.CachedData.DataHash.Value, _appliedState.ForceApplyMods);
    }

    private void LogCharacterDataChanges(CharacterDataChangeSet changes)
    {
        if (!Logger.IsEnabled(LogLevel.Debug)) return;

        foreach (var change in changes)
        {
            LogUpdatingChange(Logger, this, change.Key, string.Join(", ", change.Value));
        }
    }

    private static string DescribeSkippedOptionalIpc(IpcStatus status)
        => status.State switch
        {
            IpcState.Missing => status.Name + " missing",
            IpcState.Disabled => status.Name + " disabled",
            IpcState.VersionMismatch => status.Name + " unsupported version",
            IpcState.Error => status.Name + " error",
            _ => status.Name + " unavailable",
        };

    public override string ToString()
    {
        return Pair == null
            ? base.ToString() ?? string.Empty
            : Pair.UserData.AliasOrUID + ":" + PlayerName + ":" + (PlayerCharacter != nint.Zero ? "HasChar" : "NoChar");
    }

    internal void SetUploading(bool isUploading = true)
    {
        LogSettingUploading(Logger, this, isUploading);
        if (_charaHandler != null)
        {
            Mediator.Publish(new PlayerUploadingMessage(_charaHandler, isUploading));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _retryRegistration.Dispose();
        base.Dispose(disposing);

        if (!disposing) return;

        DisposeCoreAsync(synchronous: true).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _retryRegistration.Dispose();
        base.Dispose(disposing: true);
        await DisposeCoreAsync(synchronous: false).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }


    private async Task DisposeCoreAsync(bool synchronous)
    {
        BeginDisposal();
        var name = PlayerName;
        LogDisposing(Logger, name, Pair);
        try
        {
            Guid applicationId = Guid.NewGuid();
            if (!string.IsNullOrEmpty(name))
            {
                Mediator.Publish(new EventMessage(new Event(name, Pair.UserData, nameof(PairHandler), EventSeverity.Informational, "Disposing User")));
            }

            await _reverter.UndoApplicationAsync(applicationId).ConfigureAwait(false);

            if (synchronous)
                _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(5), nameof(PairHandler), _pairDownloadTask, _applicationTask);
            else
                await _backgroundTasks.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogErrorOnDisposal(Logger, ex, name);
        }
        finally
        {
            if (synchronous)
                PairAnalyzer.Dispose();
            else
                await PairAnalyzer.DisposeAsync().ConfigureAwait(false);
            CompleteDisposal(name);
        }
    }
    private void BeginDisposal()
    {
        _backgroundTasks.StopAccepting();
        _visibilityService.StopTracking(Pair.Ident);
        _applicationFlight.Cancel();
        _downloadFlight.Cancel();
        SetUploading(isUploading: false);
    }

    private void CompleteDisposal(string? name)
    {
        _charaHandler?.Dispose();
        _charaHandler = null;
        DisposeOwnedResources();
        PlayerName = null;
        _appliedState.CachedData = null;
        Mediator.Publish(new PairDataAppliedMessage(Pair.UserData.UID, null));
        LogDisposingComplete(Logger, name);
    }

    private void DisposeOwnedResources()
    {
        _runtimeCts.Cancel();
        _applicationFlight.Dispose();
        _downloadFlight.Dispose();
        _runtimeCts.Dispose();
        _applicationGate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Recalculating performance for {uid}")]
    private static partial void LogRecalculatingPerformance(ILogger logger, string uid);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Received data but player is in combat or performing")]
    private static partial void LogReceivedInCombat(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Received data but player was in invalid state, charaHandlerIsNull: {charaIsNull}, playerPointerIsNull: {ptrIsNull}")]
    private static partial void LogReceivedInvalidState(ILogger logger, bool charaIsNull, bool ptrIsNull);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Not applying due to hold: {reasons}")]
    private static partial void LogNotApplyingDueToHold(ILogger logger, string reasons);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Applying data for {player}, forceApplyCustomization: {forced}, forceApplyMods: {forceMods}")]
    private static partial void LogApplyingData(ILogger logger, object player, bool forced, bool forceMods);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Hash for data is {newHash}, current cache hash is {oldHash}")]
    private static partial void LogHashForData(ILogger logger, string? newHash, string oldHash);

    [LoggerMessage(Level = LogLevel.Information, Message = "Application of data for {player} while in cutscene/gpose or Penumbra/Glamourer unavailable, returning")]
    private static partial void LogApplyUnavailable(ILogger logger, object player);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Downloading and applying character for {name}")]
    private static partial void LogDownloadingAndApplying(ILogger logger, object name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Setting data: {hash}, forceApplyMods: {force}")]
    private static partial void LogSettingData(ILogger logger, string? hash, bool force);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updating {obj}/{kind} => {changes}")]
    private static partial void LogUpdatingChange(ILogger logger, object obj, ObjectKind kind, string changes);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Setting {obj} uploading {uploading}")]
    private static partial void LogSettingUploading(ILogger logger, object obj, bool uploading);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Disposing {name} ({user})")]
    private static partial void LogDisposing(ILogger logger, string? name, object user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error on disposal of {name}")]
    private static partial void LogErrorOnDisposal(ILogger logger, Exception ex, string? name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Disposing {name} complete")]
    private static partial void LogDisposingComplete(ILogger logger, string? name);
}
