using Dalamud.Game.Gui.NamePlate;
using Dalamud.Plugin.Services;
using ElezenTools.Services;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Core.Display;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Nameplates;
using Snowcloak.Game.Scheduling;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Dalamud.Game.Text.SeStringHandling;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.Services;

public sealed partial class PairDisplayDecorationService : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SnowcloakConfigService _configService;
    private readonly INamePlateGui _namePlateGui;
    private readonly IGameConfig _gameConfig;
    private readonly IPartyList _partyList;
    private readonly PairManager _pairManager;
    private readonly PairRequestService _pairRequestService;
    private readonly ApiController _apiController;
    private readonly RoleplayClientService _roleplay;
    private readonly IFrameTickHandle _tick;
    private readonly CancellationTokenSource _runtimeCts = new();

    private bool _isModified;
    private bool _isInPvP;
    private bool _namePlateRoleColorsEnabled;
    private int _redrawQueued;
    private int _disposed;

    public PairDisplayDecorationService(ILogger<PairDisplayDecorationService> logger, DalamudUtilService dalamudUtil, SnowMediator mediator,
        SnowcloakConfigService configService, INamePlateGui namePlateGui, IGameConfig gameConfig, IPartyList partyList,
        PairManager pairManager, PairRequestService pairRequestService, ApiController apiController,
        RoleplayClientService roleplay, IFrameScheduler frameScheduler)
        : base(logger, mediator)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dalamudUtil);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(namePlateGui);
        ArgumentNullException.ThrowIfNull(gameConfig);
        ArgumentNullException.ThrowIfNull(partyList);
        ArgumentNullException.ThrowIfNull(pairManager);
        ArgumentNullException.ThrowIfNull(pairRequestService);
        ArgumentNullException.ThrowIfNull(apiController);
        ArgumentNullException.ThrowIfNull(frameScheduler);

        _backgroundTasks = new BackgroundTaskTracker(logger);
        _dalamudUtil = dalamudUtil;
        _configService = configService;
        _namePlateGui = namePlateGui;
        _gameConfig = gameConfig;
        _partyList = partyList;
        _pairManager = pairManager;
        _pairRequestService = pairRequestService;
        _apiController = apiController;
        _roleplay = roleplay;

        _namePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;
        _namePlateGui.RequestRedraw();

        _tick = frameScheduler.Register("PairDisplayDecoration", TickInterval.EveryMilliseconds(200), TickPriority.Normal, GameSettingsCheck,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
        Mediator.Subscribe<PairHandlerVisibleMessage>(this, _ => RequestRedraw());
        Mediator.Subscribe<NameplateRedrawMessage>(this, _ => RequestRedraw());
        Mediator.Subscribe<PairingAvailabilityChangedMessage>(this, _ => RequestRedraw(force: true));
        Mediator.Subscribe<RpAvailabilityChangedMessage>(this, _ => RequestRedraw(force: true));
    }

    public void RequestRedraw(bool force = false)
    {
        if (!_configService.Current.UseNameColors && !_configService.Current.PairingSystemEnabled)
        {
            if (!_isModified && !force)
                return;

            _isModified = false;
        }

        if (Interlocked.Exchange(ref _redrawQueued, 1) != 0)
            return;

        _ = _backgroundTasks.Run(async ct =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                await Service.RunOnFrameworkAsync(() => _namePlateGui.RequestRedraw()).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _redrawQueued, 0);
            }
        }, nameof(RequestRedraw), _runtimeCts.Token);
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _tick.Dispose();
        base.Dispose(disposing);
        _namePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;

        _runtimeCts.Cancel();
        _backgroundTasks.StopAccepting();
        _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(2), nameof(PairDisplayDecorationService));
        TryRequestFinalRedraw();
        _runtimeCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _tick.Dispose();
        base.Dispose(disposing: true);
        _namePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;

        await _runtimeCts.CancelAsync().ConfigureAwait(false);
        await _backgroundTasks.StopAsync().ConfigureAwait(false);
        await TryRequestFinalRedrawAsync().ConfigureAwait(false);
        _runtimeCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnNamePlateUpdate(INamePlateUpdateContext _, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        var isInPvP = _dalamudUtil.IsInPvP;
        var useNameColors = _configService.Current.UseNameColors && !isInPvP;
        var usePairingHighlights = _configService.Current.PairingSystemEnabled && !isInPvP;
        var options = PairDisplayDecorationMapper.CreateOptions(_configService.Current, isInPvP, useNameColors, usePairingHighlights);

        var visiblePairs = _pairManager.GetOnlineUserPairs()
            .Where(pair => pair.IsVisible && pair.PlayerCharacterId != uint.MaxValue)
            .ToDictionary(pair => (ulong)pair.PlayerCharacterId);

        var availableForPairing = usePairingHighlights
            ? _pairRequestService.GetAvailabilityFilterSnapshot()
                .Accepted
                .Select(id => (Player: _dalamudUtil.FindPlayerByNameHash(id), Ident: id))
                .Where(candidate => candidate.Player.EntityId != 0)
                .ToDictionary(candidate => (ulong)candidate.Player.EntityId, candidate => candidate.Ident)
            : new Dictionary<ulong, string>();

        var roleplayers = _roleplay.VisibleCards
            .Where(item => item.Value is { Paused: false } && item.Value.ExpiresAtUtc > DateTimeOffset.UtcNow)
            .Select(item => (Player: _dalamudUtil.FindPlayerByNameHash(item.Key), item.Key))
            .Where(item => item.Player.EntityId != 0)
            .ToDictionary(item => (ulong)item.Player.EntityId, item => item.Key);

        var partyMembers = new HashSet<nint>();
        for (var i = 0; i < _partyList.Count; i++)
        {
            var address = _partyList[i]?.GameObject?.Address;
            if (address is { } value)
                partyMembers.Add(value);
        }

        var localPlayerAddress = _dalamudUtil.GetPlayerPointer();
        foreach (var handler in handlers)
        {
            if (handler == null)
                continue;

            if (handler.GameObject?.Address == localPlayerAddress
                && TryApplyDecoration(handler, options, PairDisplayDecorationMapper.CreateSelfSubject(_apiController.DisplayColour, _apiController.DisplayGlowColour)))
            {
                continue;
            }

            if (visiblePairs.TryGetValue(handler.GameObjectId, out var pair))
            {
                if (_namePlateRoleColorsEnabled && partyMembers.Contains(handler.GameObject?.Address ?? nint.MaxValue))
                    continue;

                TryApplyDecoration(handler, options, PairDisplayDecorationMapper.CreatePairSubject(pair, allowPairVanity: true));
            }
            else if (availableForPairing.TryGetValue(handler.GameObjectId, out var ident))
            {
                var marker = _pairRequestService.TryGetRpAvailability(ident, out var card)
                    && card is { Paused: false } && card.ExpiresAtUtc > DateTimeOffset.UtcNow
                    ? "  • RP: " + RpAvailabilityLabel(card.State)
                    : null;
                TryApplyDecoration(handler, options, PairDisplayDecorationMapper.CreatePairingCandidateSubject(), marker);
            }
            else if (roleplayers.ContainsKey(handler.GameObjectId))
            {
                var empty = new SeStringBuilder().Build();
                var card = _roleplay.VisibleCards[roleplayers[handler.GameObjectId]];
                NameplateDecorationWriter.Apply(handler, empty, empty, "  • RP: " + RpAvailabilityLabel(card.State));
                _isModified = true;
            }
        }
    }

    private static string RpAvailabilityLabel(RpAvailabilityState state) => state switch
    {
        RpAvailabilityState.OpenToWalkUps => "Walk-ups",
        RpAvailabilityState.SeekingHooks => "Hooks",
        RpAvailabilityState.InScene => "Scene",
        RpAvailabilityState.OutOfCharacter => "OOC",
        RpAvailabilityState.Away => "AFK",
        _ => "Closed",
    };

    private bool TryApplyDecoration(
        INamePlateUpdateHandler handler,
        PairDisplayDecorationOptions options,
        PairDisplayDecorationSubject subject,
        string? suffix = null)
    {
        var decoration = PairDisplayDecorationPolicy.Resolve(options, subject);
        if (decoration == null)
            return false;

        var colors = PairDisplayDecorationMapper.ToElezenColour(decoration.Value.Colour);
        NameplateDecorationWriter.Apply(
            handler,
            ElezenStrings.BuildColourStartString(colors),
            ElezenStrings.BuildColourEndString(colors), suffix);
        _isModified = true;
        return true;
    }

    private void GameSettingsCheck()
    {
        var requiresRedraw = false;

        if (_gameConfig.TryGet(Dalamud.Game.Config.UiConfigOption.NamePlateSetRoleColor, out bool namePlateRoleColorsEnabled)
            && _namePlateRoleColorsEnabled != namePlateRoleColorsEnabled)
        {
            _namePlateRoleColorsEnabled = namePlateRoleColorsEnabled;
            requiresRedraw = true;
        }

        var isInPvP = _dalamudUtil.IsInPvP;
        if (_isInPvP != isInPvP)
        {
            _isInPvP = isInPvP;
            requiresRedraw = true;
        }

        if (requiresRedraw)
            RequestRedraw(force: true);
    }

    private void TryRequestFinalRedraw()
    {
        try
        {
            Service.RunOnFrameworkAsync(() => _namePlateGui.RequestRedraw()).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            LogFinalRedrawFailure(Logger, ex);
        }
        catch (ObjectDisposedException ex)
        {
            LogFinalRedrawFailure(Logger, ex);
        }
        catch (InvalidOperationException ex)
        {
            LogFinalRedrawFailure(Logger, ex);
        }
    }

    private async Task TryRequestFinalRedrawAsync()
    {
        try
        {
            await Service.RunOnFrameworkAsync(() => _namePlateGui.RequestRedraw()).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            LogFinalRedrawFailure(Logger, ex);
        }
        catch (ObjectDisposedException ex)
        {
            LogFinalRedrawFailure(Logger, ex);
        }
        catch (InvalidOperationException ex)
        {
            LogFinalRedrawFailure(Logger, ex);
        }
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Trace, Message = "Failed to request final nameplate redraw")]
    private static partial void LogFinalRedrawFailure(ILogger logger, Exception exception);
}
