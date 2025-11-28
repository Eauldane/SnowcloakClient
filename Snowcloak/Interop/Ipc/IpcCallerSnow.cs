using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerSnow : DisposableMediatorSubscriberBase
{
    private readonly ICallGateSubscriber<List<nint>> _snowHandledGameAddresses;
    private readonly List<nint> _emptyList = [];

    private bool _pluginLoaded;

    public IpcCallerSnow(ILogger<IpcCallerSnow> logger, IDalamudPluginInterface pi,  SnowMediator mediator) : base(logger, mediator)
    {
        _snowHandledGameAddresses = pi.GetIpcSubscriber<List<nint>>("Snowcloak.GetHandledAddresses");

        _pluginLoaded = PluginWatcherService.GetInitialPluginState(pi, "Snowcloak")?.IsLoaded ?? false;

        Mediator.SubscribeKeyed<PluginChangeMessage>(this, "Snowcloak", (msg) =>
        {
            _pluginLoaded = msg.IsLoaded;
        });
    }

    public bool APIAvailable { get; private set; } = false;

    // Must be called on framework thread
    public IReadOnlyList<nint> GetHandledGameAddresses()
    {
        if (!_pluginLoaded) return _emptyList;

        try
        {
            return _snowHandledGameAddresses.InvokeFunc();
        }
        catch
        {
            return _emptyList;
        }
    }
}
