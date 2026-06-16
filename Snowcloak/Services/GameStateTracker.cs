using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using ElezenTools.Housing;
using ElezenTools.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Services;

public sealed partial class GameStateTracker : IHostedService
{
    private readonly ILogger<GameStateTracker> _logger;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IObjectTable _objectTable;
    private readonly SnowMediator _mediator;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly IFrameScheduler _frameScheduler;
    private readonly ObjectTableCache _objectTableCache;
    private readonly PlayerInteractionService _playerInteraction;
    private readonly PlotPresenceTracker _plotPresence;
    private IFrameTickHandle? _tickHandle;
    private DateTime _delayedFrameworkUpdateCheck = DateTime.UtcNow;
    private uint _lastZone;
    private bool _sentBetweenAreas;
    private bool _wasDead;
    private uint _lastTargetEntityId;

    public GameStateTracker(
        ILogger<GameStateTracker> logger,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        SnowMediator mediator,
        PerformanceCollectorService performanceCollector,
        IFrameScheduler frameScheduler,
        ObjectTableCache objectTableCache,
        PlayerInteractionService playerInteraction,
        PlotPresenceTracker plotPresence)
    {
        _logger = logger;
        _clientState = clientState;
        _condition = condition;
        _objectTable = objectTable;
        _mediator = mediator;
        _performanceCollector = performanceCollector;
        _frameScheduler = frameScheduler;
        _objectTableCache = objectTableCache;
        _playerInteraction = playerInteraction;
        _plotPresence = plotPresence;
        _tickHandle = _frameScheduler.Register("GameState", TickInterval.EveryFrame, TickPriority.Critical, FrameworkOnUpdateInternal);
    }

    public bool IsAnythingDrawing => _objectTableCache.IsAnythingDrawing;
    public bool IsInCutscene { get; private set; }
    public bool IsInGpose { get; private set; }
    public bool IsLoggedIn { get; private set; }
    public bool IsZoning => _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];
    public bool IsInCombatOrPerforming { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Service.RunOnFrameworkAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _objectTableCache.Initialise();
        }).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tickHandle?.Dispose();
        _tickHandle = null;
        return Task.CompletedTask;
    }

    public string HousingString => _plotPresence.HousingString;

    public bool TryGetLastHousingPlot(out HousingPlotLocation location) => _plotPresence.TryGetCurrentPlot(out location);

    private void FrameworkOnUpdateInternal()
    {
        var isDead = _objectTable.LocalPlayer?.IsDead ?? false;
        if (isDead != _wasDead)
        {
            _wasDead = isDead;
            if (isDead)
            {
                _frameScheduler.ActivateGate(FrameGates.Dead, nameof(FrameGates.Dead));
            }
            else
            {
                _frameScheduler.DeactivateGate(FrameGates.Dead, nameof(FrameGates.Dead));
            }
        }

        if (isDead)
        {
            return;
        }

        var isNormalFrameworkUpdate = DateTime.UtcNow < _delayedFrameworkUpdateCheck.AddMilliseconds(200);

        _performanceCollector.LogPerformance(this, $"FrameworkOnUpdateInternal+{(isNormalFrameworkUpdate ? "Regular" : "Delayed")}", () =>
        {
            _performanceCollector.LogPerformance(this, $"ObjTableToCharas", () => _objectTableCache.Refresh(_sentBetweenAreas));
            _objectTableCache.FinishDrawingPass();

            if (_clientState.IsGPosing && !IsInGpose)
            {
                LogGposeStart(_logger);
                IsInGpose = true;
                _mediator.Publish(new GposeStartMessage());
            }
            else if (!_clientState.IsGPosing && IsInGpose)
            {
                LogGposeEnd(_logger);
                IsInGpose = false;
                _mediator.Publish(new GposeEndMessage());
            }

            if ((_condition[ConditionFlag.Performing] || _condition[ConditionFlag.InCombat]) && !IsInCombatOrPerforming)
            {
                LogCombatOrPerformanceStart(_logger);
                IsInCombatOrPerforming = true;
                _mediator.Publish(new CombatOrPerformanceStartMessage());
                _mediator.Publish(new HaltScanMessage(nameof(IsInCombatOrPerforming)));
            }
            else if ((!_condition[ConditionFlag.Performing] && !_condition[ConditionFlag.InCombat]) && IsInCombatOrPerforming)
            {
                LogCombatOrPerformanceEnd(_logger);
                IsInCombatOrPerforming = false;
                _mediator.Publish(new CombatOrPerformanceEndMessage());
                _mediator.Publish(new ResumeScanMessage(nameof(IsInCombatOrPerforming)));
            }

            if (_condition[ConditionFlag.WatchingCutscene] && !IsInCutscene)
            {
                LogCutsceneStart(_logger);
                IsInCutscene = true;
                _frameScheduler.ActivateGate(FrameGates.Cutscene, nameof(FrameGates.Cutscene));
                _mediator.Publish(new CutsceneStartMessage());
                _mediator.Publish(new HaltScanMessage(nameof(IsInCutscene)));
            }
            else if (!_condition[ConditionFlag.WatchingCutscene] && IsInCutscene)
            {
                LogCutsceneEnd(_logger);
                IsInCutscene = false;
                _frameScheduler.DeactivateGate(FrameGates.Cutscene, nameof(FrameGates.Cutscene));
                _mediator.Publish(new CutsceneEndMessage());
                _mediator.Publish(new ResumeScanMessage(nameof(IsInCutscene)));
            }

            if (IsInCutscene)
            {
                return;
            }

            if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
            {
                var zone = _clientState.TerritoryType;
                if (_lastZone != zone)
                {
                    _lastZone = zone;
                    if (!_sentBetweenAreas)
                    {
                        LogZoneSwitchOrGposeStart(_logger);
                        _sentBetweenAreas = true;
                        _frameScheduler.ActivateGate(FrameGates.Zoning, nameof(FrameGates.Zoning));
                        _mediator.Publish(new ZoneSwitchStartMessage());
                        _mediator.Publish(new HaltScanMessage(nameof(ConditionFlag.BetweenAreas)));
                    }
                }

                return;
            }

            if (_sentBetweenAreas)
            {
                LogZoneSwitchOrGposeEnd(_logger);
                _sentBetweenAreas = false;
                _frameScheduler.DeactivateGate(FrameGates.Zoning, nameof(FrameGates.Zoning));
                _mediator.Publish(new ZoneSwitchEndMessage());
                _mediator.Publish(new ResumeScanMessage(nameof(ConditionFlag.BetweenAreas)));
            }

            var localPlayer = _objectTable.LocalPlayer;
            _objectTableCache.SetLocalClassJob(localPlayer);

            var target = GetTargetPlayerCharacter();
            var targetEntityId = target?.EntityId ?? 0;
            if (targetEntityId != _lastTargetEntityId)
            {
                _lastTargetEntityId = targetEntityId;
                _mediator.Publish(new TargetPlayerChangedMessage(target));
            }

            _plotPresence.Tick();

            if (isNormalFrameworkUpdate)
            {
                return;
            }

            if (localPlayer != null && localPlayer.IsValid() && !IsLoggedIn)
            {
                LogLoggedIn(_logger);
                IsLoggedIn = true;
                _lastZone = _clientState.TerritoryType;
                _mediator.Publish(new DalamudLoginMessage());
            }
            else if (localPlayer == null && IsLoggedIn)
            {
                LogLoggedOut(_logger);
                IsLoggedIn = false;
                _mediator.Publish(new DalamudLogoutMessage());
            }

            _delayedFrameworkUpdateCheck = DateTime.UtcNow;
        });
    }

    private IPlayerCharacter? GetTargetPlayerCharacter()
    {
        return _playerInteraction.GetTargetPlayerCharacter();
    }

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Gpose start")]
    private static partial void LogGposeStart(ILogger logger);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug, Message = "Gpose end")]
    private static partial void LogGposeEnd(ILogger logger);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug, Message = "Combat/Performance start")]
    private static partial void LogCombatOrPerformanceStart(ILogger logger);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Debug, Message = "Combat/Performance end")]
    private static partial void LogCombatOrPerformanceEnd(ILogger logger);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug, Message = "Cutscene start")]
    private static partial void LogCutsceneStart(ILogger logger);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Debug, Message = "Cutscene end")]
    private static partial void LogCutsceneEnd(ILogger logger);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Debug, Message = "Zone switch/Gpose start")]
    private static partial void LogZoneSwitchOrGposeStart(ILogger logger);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Debug, Message = "Zone switch/Gpose end")]
    private static partial void LogZoneSwitchOrGposeEnd(ILogger logger);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Debug, Message = "Logged in")]
    private static partial void LogLoggedIn(ILogger logger);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Debug, Message = "Logged out")]
    private static partial void LogLoggedOut(ILogger logger);
}
