using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;

using ElezenTools.Services;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerHeels : IHeelsIpc
{
    private const string IpcName = "Heels";
    private const string PluginInternalName = "SimpleHeels";
    private const string RequiredVersion = "SimpleHeels IPC 2.0";
    private const IpcCapability SupportedCapabilities = IpcCapability.HeelOffset;

    private readonly IDalamudPluginInterface _pi;
    private readonly ILogger<IpcCallerHeels> _logger;
    private readonly SnowMediator _snowMediator;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ICallGateSubscriber<(int, int)> _heelsGetApiVersion;
    private readonly ICallGateSubscriber<string> _heelsGetOffset;
    private readonly ICallGateSubscriber<string, object?> _heelsOffsetUpdate;
    private readonly ICallGateSubscriber<int, string, object?> _heelsRegisterPlayer;
    private readonly ICallGateSubscriber<int, object?> _heelsUnregisterPlayer;

    public IpcCallerHeels(ILogger<IpcCallerHeels> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil, SnowMediator snowMediator)
    {
        _pi = pi;
        _logger = logger;
        _snowMediator = snowMediator;
        _dalamudUtil = dalamudUtil;
        _heelsGetApiVersion = pi.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
        _heelsGetOffset = pi.GetIpcSubscriber<string>("SimpleHeels.GetLocalPlayer");
        _heelsRegisterPlayer = pi.GetIpcSubscriber<int, string, object?>("SimpleHeels.RegisterPlayer");
        _heelsUnregisterPlayer = pi.GetIpcSubscriber<int, object?>("SimpleHeels.UnregisterPlayer");
        _heelsOffsetUpdate = pi.GetIpcSubscriber<string, object?>("SimpleHeels.LocalChanged");

        _heelsOffsetUpdate.Subscribe(HeelsOffsetChange);

        CheckAPI();
    }

    public IpcStatus Status { get; private set; } = IpcStatus.Missing(IpcName, IpcRole.Optional, SupportedCapabilities, RequiredVersion);
    public bool APIAvailable => Status.IsAvailable;

    private void HeelsOffsetChange(string offset)
    {
        _snowMediator.Publish(new HeelsOffsetMessage());
    }

    public async Task<string> GetOffsetAsync()
    {
        if (!APIAvailable) return string.Empty;
        return await Service.RunOnFrameworkAsync(_heelsGetOffset.InvokeFunc).ConfigureAwait(false);
    }

    public async Task RestoreOffsetForPlayerAsync(IntPtr character)
    {
        if (!APIAvailable) return;
        await Service.RunOnFrameworkAsync(() =>
        {
            var gameObj = _dalamudUtil.CreateGameObject(character);
            if (gameObj != null)
            {
                _logger.LogTrace("Restoring Heels data to {chara}", character.ToString("X"));
                _heelsUnregisterPlayer.InvokeAction(gameObj.ObjectIndex);
            }
        }).ConfigureAwait(false);
    }

    public async Task SetOffsetForPlayerAsync(IntPtr character, string data)
    {
        if (!APIAvailable) return;
        await Service.RunOnFrameworkAsync(() =>
        {
            var gameObj = _dalamudUtil.CreateGameObject(character);
            if (gameObj != null)
            {
                _logger.LogTrace("Applying Heels data to {chara}", character.ToString("X"));
                _heelsRegisterPlayer.InvokeAction(gameObj.ObjectIndex, data);
            }
        }).ConfigureAwait(false);
    }

    public void CheckAPI()
    {
        try
        {
            var version = _heelsGetApiVersion.InvokeFunc();
            var statusVersion = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"IPC {version.Item1}.{version.Item2}");
            Status = version is { Item1: 2, Item2: >= 0 }
                ? IpcStatus.Available(IpcName, IpcRole.Optional, SupportedCapabilities, statusVersion)
                : IpcStatus.VersionMismatch(IpcName, IpcRole.Optional, SupportedCapabilities, statusVersion, RequiredVersion);
        }
        catch (Exception ex)
        {
            var plugin = IpcPluginProbe.Find(_pi, PluginInternalName);
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
        _heelsOffsetUpdate.Unsubscribe(HeelsOffsetChange);
    }
}
