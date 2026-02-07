using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ElezenTools.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using System.Text;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerHonorific : IIpcCaller
{
    private readonly ICallGateSubscriber<(uint major, uint minor)> _honorificApiVersion;
    private readonly ICallGateSubscriber<int, object> _honorificClearCharacterTitle;
    private readonly ICallGateSubscriber<object> _honorificDisposing;
    private readonly ICallGateSubscriber<string> _honorificGetLocalCharacterTitle;
    private readonly ICallGateSubscriber<string, object> _honorificLocalCharacterTitleChanged;
    private readonly ICallGateSubscriber<object> _honorificReady;
    private readonly ICallGateSubscriber<int, string, object> _honorificSetCharacterTitle;
    private readonly ILogger<IpcCallerHonorific> _logger;
    private readonly SnowMediator _snowMediator;
    private readonly DalamudUtilService _dalamudUtil;

    public IpcCallerHonorific(ILogger<IpcCallerHonorific> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil,
        SnowMediator snowMediator)
    {
        _logger = logger;
        _snowMediator = snowMediator;
        _dalamudUtil = dalamudUtil;
        _honorificApiVersion = pi.GetIpcSubscriber<(uint, uint)>("Honorific.ApiVersion");
        _honorificGetLocalCharacterTitle = pi.GetIpcSubscriber<string>("Honorific.GetLocalCharacterTitle");
        _honorificClearCharacterTitle = pi.GetIpcSubscriber<int, object>("Honorific.ClearCharacterTitle");
        _honorificSetCharacterTitle = pi.GetIpcSubscriber<int, string, object>("Honorific.SetCharacterTitle");
        _honorificLocalCharacterTitleChanged = pi.GetIpcSubscriber<string, object>("Honorific.LocalCharacterTitleChanged");
        _honorificDisposing = pi.GetIpcSubscriber<object>("Honorific.Disposing");
        _honorificReady = pi.GetIpcSubscriber<object>("Honorific.Ready");

        _honorificLocalCharacterTitleChanged.Subscribe(OnHonorificLocalCharacterTitleChanged);
        _honorificDisposing.Subscribe(OnHonorificDisposing);
        _honorificReady.Subscribe(OnHonorificReady);

        CheckAPI();
    }

    public bool APIAvailable { get; private set; } = false;

    public void CheckAPI()
    {
        try
        {
            APIAvailable = _honorificApiVersion.InvokeFunc() is { Item1: 3, Item2: >= 0 };
        }
        catch
        {
            APIAvailable = false;
        }
    }

    public void Dispose()
    {
        _honorificLocalCharacterTitleChanged.Unsubscribe(OnHonorificLocalCharacterTitleChanged);
        _honorificDisposing.Unsubscribe(OnHonorificDisposing);
        _honorificReady.Unsubscribe(OnHonorificReady);
    }

    public async Task ClearTitleAsync(nint character)
    {
        if (!APIAvailable) return;
        await Service.UseFramework(() =>
        {
            var gameObj = _dalamudUtil.CreateGameObject(character);
            if (gameObj is IPlayerCharacter c)
            {
                _logger.LogTrace("Honorific removing for {addr}", c.Address.ToString("X"));
                _honorificClearCharacterTitle!.InvokeAction(c.ObjectIndex);
            }
        }).ConfigureAwait(false);
    }

    public async Task<string> GetTitle()
    {
        if (!APIAvailable) return string.Empty;
        return await Service.UseFramework(() =>
        {
            string title = _honorificGetLocalCharacterTitle.InvokeFunc();
            return string.IsNullOrEmpty(title) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(title));
        }).ConfigureAwait(false);
    }

    public async Task SetTitleAsync(IntPtr character, string honorificDataB64)
    {
        if (!APIAvailable) return;
        _logger.LogTrace("Applying Honorific data to {chara}", character.ToString("X"));
        try
        {
            await Service.UseFramework(() =>
            {
                var gameObj = _dalamudUtil.CreateGameObject(character);
                if (gameObj is IPlayerCharacter pc)
                {
                    string honorificData = string.IsNullOrEmpty(honorificDataB64) ? string.Empty : Encoding.UTF8.GetString(Convert.FromBase64String(honorificDataB64));
                    if (string.IsNullOrEmpty(honorificData))
                    {
                        _honorificClearCharacterTitle!.InvokeAction(pc.ObjectIndex);
                    }
                    else
                    {
                        _honorificSetCharacterTitle!.InvokeAction(pc.ObjectIndex, honorificData);
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not apply Honorific data");
        }
    }

    private void OnHonorificDisposing()
    {
        _snowMediator.Publish(new HonorificMessage(string.Empty));
    }

    private void OnHonorificLocalCharacterTitleChanged(string titleJson)
    {
        string titleData = string.IsNullOrEmpty(titleJson) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(titleJson));
        _snowMediator.Publish(new HonorificMessage(titleData));
    }

    private void OnHonorificReady()
    {
        CheckAPI();
        _snowMediator.Publish(new HonorificReadyMessage());
    }
}
