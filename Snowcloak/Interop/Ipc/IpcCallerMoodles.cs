using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ElezenTools.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerMoodles : IMoodlesIpc
{
    private const string IpcName = "Moodles";
    private const string RequiredVersion = "IPC 4";
    private const IpcCapability SupportedCapabilities = IpcCapability.Moodles;

    private readonly IDalamudPluginInterface _pi;
    private readonly ICallGateSubscriber<int> _moodlesApiVersion;
    private readonly ICallGateSubscriber<nint, object> _moodlesOnChange;
    private readonly ICallGateSubscriber<nint, string> _moodlesGetStatus;
    private readonly ICallGateSubscriber<nint, string, object> _moodlesSetStatus;
    private readonly ICallGateSubscriber<nint, object> _moodlesRevertStatus;
    private readonly ILogger<IpcCallerMoodles> _logger;
    private readonly SnowMediator _snowMediator;

    public IpcCallerMoodles(ILogger<IpcCallerMoodles> logger, IDalamudPluginInterface pi,
        SnowMediator snowMediator)
    {
        _pi = pi;
        _logger = logger;
        _snowMediator = snowMediator;

        _moodlesApiVersion = pi.GetIpcSubscriber<int>("Moodles.Version");
        _moodlesOnChange = pi.GetIpcSubscriber<nint, object>("Moodles.StatusManagerModified");
        _moodlesGetStatus = pi.GetIpcSubscriber<nint, string>("Moodles.GetStatusManagerByPtrV2");
        _moodlesSetStatus = pi.GetIpcSubscriber<nint, string, object>("Moodles.SetStatusManagerByPtrV2");
        _moodlesRevertStatus = pi.GetIpcSubscriber<nint, object>("Moodles.ClearStatusManagerByPtrV2");

        _moodlesOnChange.Subscribe(OnMoodlesChange);

        CheckAPI();
    }

    private void OnMoodlesChange(nint address)
    {
        _snowMediator.Publish(new MoodlesMessage(address));
    }

    public IpcStatus Status { get; private set; } = IpcStatus.Missing(IpcName, IpcRole.Optional, SupportedCapabilities, RequiredVersion);
    public bool APIAvailable => Status.IsAvailable;

    public void CheckAPI()
    {
        try
        {
            var version = _moodlesApiVersion.InvokeFunc();
            var statusVersion = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"IPC {version}");
            Status = version == 4
                ? IpcStatus.Available(IpcName, IpcRole.Optional, SupportedCapabilities, statusVersion)
                : IpcStatus.VersionMismatch(IpcName, IpcRole.Optional, SupportedCapabilities, statusVersion, RequiredVersion);
        }
        catch (Exception ex)
        {
            var plugin = IpcPluginProbe.Find(_pi, IpcName);
            Status = plugin switch
            {
                { IsInstalled: false } => IpcStatus.Missing(IpcName, IpcRole.Optional, SupportedCapabilities, RequiredVersion),
                { IsLoaded: false } => IpcStatus.Disabled(IpcName, IpcRole.Optional, SupportedCapabilities, plugin.Version?.ToString(), "plugin is installed but not loaded"),
                _ => IpcStatus.Error(IpcName, IpcRole.Optional, SupportedCapabilities, ex.Message, plugin.Version?.ToString(), RequiredVersion),
            };
        }
    }

    public void Dispose()
    {
        _moodlesOnChange.Unsubscribe(OnMoodlesChange);
    }

    public async Task<string?> GetStatusAsync(nint address)
    {
        if (!APIAvailable) return null;

        try
        {
            return await Service.RunOnFrameworkAsync(() => _moodlesGetStatus.InvokeFunc(address)).ConfigureAwait(false);

        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not Get Moodles Status");
            return null;
        }
    }

    public async Task SetStatusAsync(nint pointer, string status)
    {
        if (!APIAvailable) return;
        try
        {
            await Service.RunOnFrameworkAsync(() => _moodlesSetStatus.InvokeAction(pointer, status)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not Set Moodles Status");
        }
    }

    public async Task RevertStatusAsync(nint pointer)
    {
        if (!APIAvailable) return;
        try
        {
            await Service.RunOnFrameworkAsync(() => _moodlesRevertStatus.InvokeAction(pointer)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not Set Moodles Status");
        }
    }
}
