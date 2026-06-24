using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using ElezenTools.UI.Mvu;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Pairing;
using Snowcloak.UI.PairingAvailability;
using System;
using System.Numerics;

namespace Snowcloak.UI;


public sealed class PairingAvailabilityWindow : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly AvailabilityDispatcher _dispatcher;
    private readonly StoreViewHost<AvailabilityViewState> _host;
    private readonly TitleBarButton _lockButton;
    private bool _locked;

    public PairingAvailabilityWindow(ILogger<PairingAvailabilityWindow> logger, SnowMediator mediator,
        PairRequestService pairRequestService, DalamudUtilService dalamudUtilService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Frostbrand - Open to Pairs###SnowcloakPairingAvailability", performanceCollectorService)
    {
        ArgumentNullException.ThrowIfNull(pairRequestService);

        var store = pairRequestService.AvailabilityStore;
        _dispatcher = new AvailabilityDispatcher(logger, pairRequestService, dalamudUtilService, mediator);
        _host = new StoreViewHost<AvailabilityViewState>(store, new PairingAvailabilityView(), _dispatcher,
            state => SyncLockButton(state.Locked));
        store.RefreshState();

        SetScaledSizeConstraints(new Vector2(350, 200));

        RespectCloseHotkey = true;

        TitleBarButtons.Add(new TitleBarButton()
        {
            ShowTooltip = () => ImGui.SetTooltip("Refresh list of nearby players"),
            Click = _ => _dispatcher.Dispatch(new RefreshAvailabilityIntent()),
            Icon = FontAwesomeIcon.SyncAlt
        });

        _lockButton = new TitleBarButton()
        {
            ShowTooltip = () => ImGui.SetTooltip(_locked ? "Unlock to resume live updates" : "Lock list to pause updates"),
            Click = _ => _dispatcher.Dispatch(new SetLockedIntent(!_locked)),
            Icon = FontAwesomeIcon.LockOpen
        };
        TitleBarButtons.Add(_lockButton);
    }

    protected override void DrawInternal()
    {
        _host.Draw();
    }

    private void SyncLockButton(bool locked)
    {
        _locked = locked;
        _lockButton.Icon = locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen;
    }

    protected override void Dispose(bool disposing)
    {
        _host.Dispose();
        base.Dispose(disposing);
    }
}
