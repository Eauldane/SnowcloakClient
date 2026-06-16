using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using System.Text;

using ElezenTools.Services;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerCustomize : ICustomizePlusIpc
{
    private const string IpcName = "CustomizePlus";
    private const string RequiredVersion = "IPC 6.0";
    private const IpcCapability SupportedCapabilities = IpcCapability.BodyScale;

    private readonly IDalamudPluginInterface _pi;
    private readonly ICallGateSubscriber<(int, int)> _customizePlusApiVersion;
    private readonly ICallGateSubscriber<ushort, (int, Guid?)> _customizePlusGetActiveProfile;
    private readonly ICallGateSubscriber<Guid, (int, string?)> _customizePlusGetProfileById;
    private readonly ICallGateSubscriber<ushort, Guid, object> _customizePlusOnScaleUpdate;
    private readonly ICallGateSubscriber<ushort, int> _customizePlusRevertCharacter;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> _customizePlusSetBodyScaleToCharacter;
    private readonly ICallGateSubscriber<Guid, int> _customizePlusDeleteByUniqueId;
    private readonly ILogger<IpcCallerCustomize> _logger;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SnowMediator _snowMediator;

    public IpcCallerCustomize(ILogger<IpcCallerCustomize> logger, IDalamudPluginInterface dalamudPluginInterface,
        DalamudUtilService dalamudUtil, SnowMediator snowMediator)
    {
        _pi = dalamudPluginInterface;
        _customizePlusApiVersion = dalamudPluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        _customizePlusGetActiveProfile = dalamudPluginInterface.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        _customizePlusGetProfileById = dalamudPluginInterface.GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId");
        _customizePlusRevertCharacter = dalamudPluginInterface.GetIpcSubscriber<ushort, int>("CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter");
        _customizePlusSetBodyScaleToCharacter = dalamudPluginInterface.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        _customizePlusOnScaleUpdate = dalamudPluginInterface.GetIpcSubscriber<ushort, Guid, object>("CustomizePlus.Profile.OnUpdate");
        _customizePlusDeleteByUniqueId = dalamudPluginInterface.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DeleteTemporaryProfileByUniqueId");

        _customizePlusOnScaleUpdate.Subscribe(OnCustomizePlusScaleChange);
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _snowMediator = snowMediator;

        CheckAPI();
    }

    public IpcStatus Status { get; private set; } = IpcStatus.Missing(IpcName, IpcRole.Optional, SupportedCapabilities, RequiredVersion);
    public bool APIAvailable => Status.IsAvailable;

    public async Task RevertAsync(nint character)
    {
        if (!APIAvailable) return;
        await Service.RunOnFrameworkAsync(() =>
        {
            var gameObj = _dalamudUtil.CreateGameObject(character);
            if (gameObj is ICharacter c)
            {
                _logger.LogTrace("CustomizePlus reverting for {chara}", c.Address.ToString("X"));
                _customizePlusRevertCharacter!.InvokeFunc(c.ObjectIndex);
            }
        }).ConfigureAwait(false);
    }

    public async Task<Guid?> SetBodyScaleAsync(nint character, string scale)
    {
        if (!APIAvailable) return null;
        return await Service.RunOnFrameworkAsync(() =>
        {
            var gameObj = _dalamudUtil.CreateGameObject(character);
            if (gameObj is ICharacter c)
            {
                string decodedScale = Encoding.UTF8.GetString(Convert.FromBase64String(scale));
                _logger.LogTrace("CustomizePlus applying for {chara}", c.Address.ToString("X"));
                if (scale.IsNullOrEmpty())
                {
                    _customizePlusRevertCharacter!.InvokeFunc(c.ObjectIndex);
                    return null;
                }
                else
                {
                    var result = _customizePlusSetBodyScaleToCharacter!.InvokeFunc(c.ObjectIndex, decodedScale);
                    return result.Item2;
                }
            }

            return null;
        }).ConfigureAwait(false);
    }

    public async Task RevertByIdAsync(Guid? profileId)
    {
        if (!APIAvailable || profileId == null) return;

        await Service.RunOnFrameworkAsync(() =>
        {
            _ = _customizePlusDeleteByUniqueId.InvokeFunc(profileId.Value);
        }).ConfigureAwait(false);
    }

    public async Task<string?> GetScaleAsync(nint character)
    {
        if (!APIAvailable) return null;
        var scale = await Service.RunOnFrameworkAsync(() =>
        {
            var gameObj = _dalamudUtil.CreateGameObject(character);
            if (gameObj is ICharacter c)
            {
                var res = _customizePlusGetActiveProfile.InvokeFunc(c.ObjectIndex);
                _logger.LogTrace("CustomizePlus GetActiveProfile returned {err}", res.Item1);
                if (res.Item1 != 0 || res.Item2 == null) return string.Empty;
                return _customizePlusGetProfileById.InvokeFunc(res.Item2.Value).Item2;
            }

            return string.Empty;
        }).ConfigureAwait(false);
        if (string.IsNullOrEmpty(scale)) return string.Empty;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(scale));
    }

    public void CheckAPI()
    {
        try
        {
            var version = _customizePlusApiVersion.InvokeFunc();
            var statusVersion = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"IPC {version.Item1}.{version.Item2}");
            Status = version is { Item1: 6, Item2: >= 0 }
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

    private void OnCustomizePlusScaleChange(ushort c, Guid g)
    {
        var obj = _dalamudUtil.GetCharacterFromObjectTableByIndex(c);
        _snowMediator.Publish(new CustomizePlusMessage(obj?.Address ?? null));
    }

    public void Dispose()
    {
        _customizePlusOnScaleUpdate.Unsubscribe(OnCustomizePlusScaleChange);
    }
}
