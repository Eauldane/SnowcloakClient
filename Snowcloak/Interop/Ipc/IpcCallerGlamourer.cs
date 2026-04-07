using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ElezenTools.Services;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerGlamourer : DisposableMediatorSubscriberBase, IIpcCaller
{
    private enum AppearanceBackend
    {
        None,
        Glamourer,
        Armoire,
    }

    private const string ArmoireInternalName = "Armoire";
    private const string ArmoireApiVersionLabel = "Armoire.ApiVersion.V1";
    private const string ArmoireInitializedLabel = "Armoire.Initialized.V1";
    private const string ArmoireDisposedLabel = "Armoire.Disposed.V1";
    private const string ArmoireStateChangedLabel = "Armoire.StateChanged.V1";
    private const string ArmoireGetStateBase64Label = "Armoire.GetStateBase64.V1";
    private const string ArmoireGetStateBase64NameLabel = "Armoire.GetStateBase64Name.V1";
    private const string ArmoireApplyStateLabel = "Armoire.ApplyState.V1";
    private const string ArmoireApplyStateNameLabel = "Armoire.ApplyStateName.V1";
    private const string ArmoireRevertStateLabel = "Armoire.RevertState.V1";
    private const string ArmoireRevertStateNameLabel = "Armoire.RevertStateName.V1";
    private const string ArmoireUnlockStateLabel = "Armoire.UnlockState.V1";
    private const string ArmoireUnlockStateNameLabel = "Armoire.UnlockStateName.V1";

    private const uint LockCode = 0x6D617265;

    private readonly ILogger<IpcCallerGlamourer> _logger;
    private readonly IDalamudPluginInterface _pi;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SnowMediator _snowMediator;
    private readonly RedrawManager _redrawManager;

    private readonly ApiVersion _glamourerApiVersions;
    private readonly ApplyState _glamourerApplyAll;
    private readonly GetStateBase64 _glamourerGetAllCustomization;
    private readonly RevertState _glamourerRevert;
    private readonly RevertStateName _glamourerRevertByName;
    private readonly UnlockState _glamourerUnlock;
    private readonly UnlockStateName _glamourerUnlockByName;
    private readonly EventSubscriber<nint> _glamourerStateChanged;

    private readonly ICallGateSubscriber<Version> _armoireApiVersion;
    private readonly ICallGateSubscriber<object> _armoireInitialized;
    private readonly ICallGateSubscriber<object> _armoireDisposed;
    private readonly ICallGateSubscriber<nint, object> _armoireStateChanged;
    private readonly ICallGateSubscriber<int, uint, (int, string?)> _armoireGetAllCustomization;
    private readonly ICallGateSubscriber<object, int, uint, ulong, int> _armoireApplyAll;
    private readonly ICallGateSubscriber<int, uint, ulong, int> _armoireRevert;
    private readonly ICallGateSubscriber<string, uint, ulong, int> _armoireRevertByName;
    private readonly ICallGateSubscriber<int, uint, int> _armoireUnlock;
    private readonly ICallGateSubscriber<string, uint, int> _armoireUnlockByName;

    private bool _shownGlamourerUnavailable;
    private AppearanceBackend _backend = AppearanceBackend.None;

    public IpcCallerGlamourer(ILogger<IpcCallerGlamourer> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil, SnowMediator snowMediator,
        RedrawManager redrawManager) : base(logger, snowMediator)
    {
        _logger = logger;
        _pi = pi;
        _dalamudUtil = dalamudUtil;
        _snowMediator = snowMediator;
        _redrawManager = redrawManager;

        _glamourerApiVersions = new ApiVersion(pi);
        _glamourerGetAllCustomization = new GetStateBase64(pi);
        _glamourerApplyAll = new ApplyState(pi);
        _glamourerRevert = new RevertState(pi);
        _glamourerRevertByName = new RevertStateName(pi);
        _glamourerUnlock = new UnlockState(pi);
        _glamourerUnlockByName = new UnlockStateName(pi);
        _glamourerStateChanged = StateChanged.Subscriber(pi, OnGlamourerStateChanged);
        _glamourerStateChanged.Enable();

        _armoireApiVersion = pi.GetIpcSubscriber<Version>(ArmoireApiVersionLabel);
        _armoireInitialized = pi.GetIpcSubscriber<object>(ArmoireInitializedLabel);
        _armoireDisposed = pi.GetIpcSubscriber<object>(ArmoireDisposedLabel);
        _armoireStateChanged = pi.GetIpcSubscriber<nint, object>(ArmoireStateChangedLabel);
        _armoireGetAllCustomization = pi.GetIpcSubscriber<int, uint, (int, string?)>(ArmoireGetStateBase64Label);
        _armoireApplyAll = pi.GetIpcSubscriber<object, int, uint, ulong, int>(ArmoireApplyStateLabel);
        _armoireRevert = pi.GetIpcSubscriber<int, uint, ulong, int>(ArmoireRevertStateLabel);
        _armoireRevertByName = pi.GetIpcSubscriber<string, uint, ulong, int>(ArmoireRevertStateNameLabel);
        _armoireUnlock = pi.GetIpcSubscriber<int, uint, int>(ArmoireUnlockStateLabel);
        _armoireUnlockByName = pi.GetIpcSubscriber<string, uint, int>(ArmoireUnlockStateNameLabel);

        _armoireInitialized.Subscribe(OnArmoireInitialized);
        _armoireDisposed.Subscribe(OnArmoireDisposed);
        _armoireStateChanged.Subscribe(OnArmoireStateChanged);

        CheckAPI();
        Mediator.Subscribe<DalamudLoginMessage>(this, _ => _shownGlamourerUnavailable = false);
    }

    public bool APIAvailable { get; private set; }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _redrawManager.Cancel();
        _glamourerStateChanged.Dispose();
        _armoireInitialized.Unsubscribe(OnArmoireInitialized);
        _armoireDisposed.Unsubscribe(OnArmoireDisposed);
        _armoireStateChanged.Unsubscribe(OnArmoireStateChanged);
    }

    public void CheckAPI()
    {
        var glamourerPluginVersion = _pi.InstalledPlugins
            .FirstOrDefault(p => string.Equals(p.InternalName, "Glamourer", StringComparison.OrdinalIgnoreCase))
            ?.Version ?? new Version(0, 0, 0, 0);
        var armoirePluginVersion = _pi.InstalledPlugins
            .FirstOrDefault(p => string.Equals(p.InternalName, ArmoireInternalName, StringComparison.OrdinalIgnoreCase))
            ?.Version ?? new Version(0, 0, 0, 0);

        var glamourerAvailable = false;
        if (glamourerPluginVersion >= new Version(1, 3, 0, 10))
        {
            try
            {
                var version = _glamourerApiVersions.Invoke();
                glamourerAvailable = version is { Major: 1, Minor: >= 1 };
            }
            catch
            {
                glamourerAvailable = false;
            }
        }

        var armoireAvailable = false;
        if (armoirePluginVersion >= new Version(0, 1, 0, 0))
        {
            try
            {
                var version = _armoireApiVersion.InvokeFunc();
                armoireAvailable = version.Major >= 1;
            }
            catch
            {
                armoireAvailable = false;
            }
        }

        _backend = glamourerAvailable
            ? AppearanceBackend.Glamourer
            : armoireAvailable
                ? AppearanceBackend.Armoire
                : AppearanceBackend.None;

        APIAvailable = _backend != AppearanceBackend.None;
        _shownGlamourerUnavailable = _shownGlamourerUnavailable && !APIAvailable;

        if (!APIAvailable && !_shownGlamourerUnavailable)
        {
            _shownGlamourerUnavailable = true;
            _snowMediator.Publish(new NotificationMessage(
                "Glamourer inactive",
                "Neither Glamourer nor Armoire is active or current enough for Snowcloak. Update Glamourer or load Armoire to continue.",
                NotificationType.Error));
        }
    }

    public async Task ApplyAllAsync(ILogger logger, GameObjectHandler handler, string? customization, Guid applicationId, CancellationToken token, bool fireAndForget = false)
    {
        if (!APIAvailable || string.IsNullOrEmpty(customization) || _dalamudUtil.IsZoning)
        {
            return;
        }

        await _redrawManager.RedrawSemaphore.WaitAsync(token).ConfigureAwait(false);

        try
        {
            await _redrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, chara =>
            {
                try
                {
                    logger.LogDebug("[{appid}] Calling appearance apply on {backend}", applicationId, _backend);
                    if (_backend == AppearanceBackend.Glamourer)
                    {
                        _glamourerApplyAll.Invoke(customization, chara.ObjectIndex, LockCode);
                    }
                    else
                    {
                        _armoireApplyAll.InvokeFunc(customization, chara.ObjectIndex, LockCode, 0);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{appid}] Failed to apply appearance data", applicationId);
                }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _redrawManager.RedrawSemaphore.Release();
        }
    }

    public async Task<string> GetCharacterCustomizationAsync(IntPtr character)
    {
        if (!APIAvailable)
        {
            return string.Empty;
        }

        try
        {
            return await Service.UseFramework(() =>
            {
                var gameObj = _dalamudUtil.CreateGameObject(character);
                if (gameObj is not ICharacter c)
                {
                    return string.Empty;
                }

                return _backend == AppearanceBackend.Glamourer
                    ? _glamourerGetAllCustomization.Invoke(c.ObjectIndex).Item2 ?? string.Empty
                    : _armoireGetAllCustomization.InvokeFunc(c.ObjectIndex, 0).Item2 ?? string.Empty;
            }).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task RevertAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning)
        {
            return;
        }

        try
        {
            await _redrawManager.RedrawSemaphore.WaitAsync(token).ConfigureAwait(false);
            await _redrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, chara =>
            {
                try
                {
                    logger.LogDebug("[{appid}] Reverting appearance on {backend}", applicationId, _backend);
                    if (_backend == AppearanceBackend.Glamourer)
                    {
                        _glamourerUnlock.Invoke(chara.ObjectIndex, LockCode);
                        _glamourerRevert.Invoke(chara.ObjectIndex, LockCode);
                    }
                    else
                    {
                        _armoireUnlock.InvokeFunc(chara.ObjectIndex, LockCode);
                        _armoireRevert.InvokeFunc(chara.ObjectIndex, LockCode, 0);
                    }

                    _snowMediator.Publish(new PenumbraRedrawCharacterMessage(chara));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{appid}] Error during appearance revert", applicationId);
                }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _redrawManager.RedrawSemaphore.Release();
        }
    }

    public async Task RevertByNameAsync(ILogger logger, string name, Guid applicationId)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning)
        {
            return;
        }

        await Service.UseFramework(() => RevertByName(logger, name, applicationId)).ConfigureAwait(false);
    }

    public void RevertByName(ILogger logger, string name, Guid applicationId)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning)
        {
            return;
        }

        try
        {
            logger.LogDebug("[{appid}] Reverting appearance by name on {backend}", applicationId, _backend);
            if (_backend == AppearanceBackend.Glamourer)
            {
                _glamourerRevertByName.Invoke(name, LockCode);
                _glamourerUnlockByName.Invoke(name, LockCode);
            }
            else
            {
                _armoireRevertByName.InvokeFunc(name, LockCode, 0);
                _armoireUnlockByName.InvokeFunc(name, LockCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during appearance RevertByName");
        }
    }

    private void GlamourerChanged(nint address)
        => _snowMediator.Publish(new GlamourerChangedMessage(address));

    private void OnGlamourerStateChanged(nint address)
    {
        if (_backend == AppearanceBackend.Glamourer)
        {
            GlamourerChanged(address);
        }
    }

    private void OnArmoireInitialized()
        => CheckAPI();

    private void OnArmoireDisposed()
        => CheckAPI();

    private void OnArmoireStateChanged(nint address)
    {
        if (_backend == AppearanceBackend.Armoire)
        {
            GlamourerChanged(address);
        }
    }
}
