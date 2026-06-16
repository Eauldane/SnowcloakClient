using System.Globalization;
using ElezenTools.UI.Mvu;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Pairing;

namespace Snowcloak.UI.PairingAvailability;

public sealed class AvailabilityDispatcher : IDispatcher
{
    private static readonly Action<ILogger, string, Exception?> LogUnhandledIntent =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(LogUnhandledIntent)),
            "Unhandled availability intent {Intent}");
    private static readonly Action<ILogger, Exception?> LogAvailabilityIntentFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2, nameof(LogAvailabilityIntentFailed)),
            "Availability intent failed");

    private readonly ILogger _logger;
    private readonly PairRequestService _pairRequestService;
    private readonly PairingAvailabilityStore _store;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SnowMediator _mediator;

    public AvailabilityDispatcher(ILogger logger, PairRequestService pairRequestService,
        DalamudUtilService dalamudUtil, SnowMediator mediator)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pairRequestService);
        ArgumentNullException.ThrowIfNull(dalamudUtil);
        ArgumentNullException.ThrowIfNull(mediator);

        _logger = logger;
        _pairRequestService = pairRequestService;
        _store = pairRequestService.AvailabilityStore;
        _dalamudUtil = dalamudUtil;
        _mediator = mediator;
    }

    public void Dispatch(IIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        switch (intent)
        {
            case SendPairRequestIntent send:
                Run(() => _pairRequestService.SendPairRequestAsync(send.Ident));
                break;
            case ViewProfileIntent view:
                Run(() => _pairRequestService.RequestProfileAsync(view.Ident));
                break;
            case ExaminePlayerIntent examine:
                Run(() => ExamineAsync(examine.Ident, examine.DisplayName));
                break;
            case OpenAdventurerPlateIntent plate:
                Run(() => AdventurerPlateAsync(plate.Ident));
                break;
            case SetSearchQueryIntent search:
                _store.SetSearchQuery(search.Query);
                break;
            case SetTagQueryIntent tag:
                _store.SetTagQuery(tag.Query);
                break;
            case SetOnlyWithProfilesIntent onlyProfiles:
                _store.SetOnlyWithProfiles(onlyProfiles.Value);
                break;
            case SetUseProfileCardsIntent useCards:
                _store.SetUseProfileCards(useCards.Value);
                break;
            case SetLockedIntent locked:
                _store.SetLocked(locked.Locked);
                break;
            case RefreshAvailabilityIntent:
                Run(RefreshAsync);
                break;
            case RespondPairRequestIntent response:
                Run(() => _pairRequestService.RespondAsync(response.RequestId, response.Accepted));
                break;
            case SetPairingEnabledIntent pairing:
                _pairRequestService.SetPairingSystemEnabled(pairing.Enabled);
                break;
            case OpenFrostbrandPanelIntent:
                _mediator.Publish(new OpenFrostbrandUiMessage());
                break;
            case ToggleAvailabilityWindowIntent:
                _mediator.Publish(new UiToggleMessage(typeof(Snowcloak.UI.PairingAvailabilityWindow)));
                break;
            default:
                LogUnhandledIntent(_logger, intent.GetType().Name, null);
                break;
        }
    }

    private async Task ExamineAsync(string ident, string displayName)
    {
        var success = await _dalamudUtil.ExaminePlayerByIdentAsync(ident).ConfigureAwait(false);
        _mediator.Publish(success
            ? new NotificationMessage("Examine",
                string.Format(CultureInfo.InvariantCulture, "Opening examination for {0}.", displayName),
                NotificationType.Info, TimeSpan.FromSeconds(4))
            : new NotificationMessage("Examine failed",
                "Could not find that player nearby.", NotificationType.Warning, TimeSpan.FromSeconds(4)));
    }

    private async Task AdventurerPlateAsync(string ident)
    {
        var success = await _dalamudUtil.OpenAdventurerPlateByIdentAsync(ident).ConfigureAwait(false);
        if (!success)
            _mediator.Publish(new NotificationMessage("Adventurer Plate failed",
                "Could not find that player nearby.", NotificationType.Warning, TimeSpan.FromSeconds(4)));
    }

    private async Task RefreshAsync()
    {
        await _pairRequestService.RefreshNearbyAvailabilityAsync(force: true).ConfigureAwait(false);
        _store.RecaptureLockIfLocked();
    }

    private void Run(Func<Task> operation) => _ = ObserveAsync(operation);

    private async Task ObserveAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAvailabilityIntentFailed(_logger, ex);
        }
    }
}
