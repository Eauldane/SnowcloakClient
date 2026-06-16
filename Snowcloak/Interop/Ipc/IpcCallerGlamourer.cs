using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;

using ElezenTools.Services;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerGlamourer : DisposableMediatorSubscriberBase, IGlamourerIpc
{
    private const string IpcName = "Glamourer";
    private const string RequiredVersion = "plugin 1.6.1.7, IPC 1.1";
    private static readonly Version MinimumPluginVersion = new(1, 6, 1, 7);
    private const IpcCapability SupportedCapabilities = IpcCapability.Appearance;

    private enum AppearanceBackend
    {
        None,
        Glamourer,
    }

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

        CheckAPI();
        Mediator.Subscribe<DalamudLoginMessage>(this, _ => _shownGlamourerUnavailable = false);
    }

    public IpcStatus Status { get; private set; } = IpcStatus.Missing(IpcName, IpcRole.Required, SupportedCapabilities, RequiredVersion);
    public bool APIAvailable => Status.IsAvailable;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _redrawManager.Cancel();
        _glamourerStateChanged.Dispose();
    }

    public void CheckAPI()
    {
        var glamourerPlugin = IpcPluginProbe.Find(_pi, IpcName);
        var version = glamourerPlugin.Version?.ToString();

        if (!glamourerPlugin.IsInstalled)
        {
            Status = IpcStatus.Missing(IpcName, IpcRole.Required, SupportedCapabilities, RequiredVersion);
            _backend = AppearanceBackend.None;
        }
        else if (!glamourerPlugin.IsLoaded)
        {
            Status = IpcStatus.Disabled(IpcName, IpcRole.Required, SupportedCapabilities, version, "plugin is installed but not loaded");
            _backend = AppearanceBackend.None;
        }
        else if (glamourerPlugin.Version!.CompareTo(MinimumPluginVersion) < 0)
        {
            Status = IpcStatus.VersionMismatch(IpcName, IpcRole.Required, SupportedCapabilities, version, RequiredVersion);
            _backend = AppearanceBackend.None;
        }
        else
        {
            try
            {
                var apiVersion = _glamourerApiVersions.Invoke();
                var glamourerAvailable = apiVersion is { Major: 1, Minor: >= 1 };
                var statusVersion = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{version}; IPC {apiVersion.Major}.{apiVersion.Minor}");
                Status = glamourerAvailable
                    ? IpcStatus.Available(IpcName, IpcRole.Required, SupportedCapabilities, statusVersion)
                    : IpcStatus.VersionMismatch(IpcName, IpcRole.Required, SupportedCapabilities, statusVersion, RequiredVersion);
                _backend = glamourerAvailable ? AppearanceBackend.Glamourer : AppearanceBackend.None;
            }
            catch (Exception ex)
            {
                Status = IpcStatus.Error(IpcName, IpcRole.Required, SupportedCapabilities, ex.Message, version, RequiredVersion);
                _backend = AppearanceBackend.None;
            }
        }
        _shownGlamourerUnavailable = _shownGlamourerUnavailable && !APIAvailable;

        if (!APIAvailable && !_shownGlamourerUnavailable)
        {
            _shownGlamourerUnavailable = true;
            _snowMediator.Publish(new NotificationMessage(
                "Glamourer inactive",
                "Glamourer is not active or current enough for Snowcloak. Update Glamourer to continue.",
                NotificationType.Error));
        }
    }

    public async Task ApplyAllAsync(ILogger logger, IGameObjectHandle handler, string? customization, Guid applicationId, CancellationToken token, bool fireAndForget = false)
    {
        if (!APIAvailable || string.IsNullOrEmpty(customization) || _dalamudUtil.IsZoning)
        {
            return;
        }

        await _redrawManager.RunWithRedrawSlotAsync(logger, (GameObjectHandler)handler, applicationId, chara =>
        {
            try
            {
                logger.LogDebug("[{appid}] Calling appearance apply on {backend}", applicationId, _backend);
                _glamourerApplyAll.Invoke(customization, chara.ObjectIndex, LockCode);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{appid}] Failed to apply appearance data", applicationId);
            }
        }, token).ConfigureAwait(false);
    }

    public async Task<string> GetCharacterCustomizationAsync(IntPtr character)
    {
        if (!APIAvailable)
        {
            return string.Empty;
        }

        try
        {
            return await Service.RunOnFrameworkAsync(() =>
            {
                var gameObj = _dalamudUtil.CreateGameObject(character);
                if (gameObj is not ICharacter c)
                {
                    return string.Empty;
                }

                return _glamourerGetAllCustomization.Invoke(c.ObjectIndex).Item2 ?? string.Empty;
            }).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task RevertAsync(ILogger logger, IGameObjectHandle handler, Guid applicationId, CancellationToken token)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning)
        {
            return;
        }

        await _redrawManager.RunWithRedrawSlotAsync(logger, (GameObjectHandler)handler, applicationId, chara =>
        {
            try
            {
                logger.LogDebug("[{appid}] Reverting appearance on {backend}", applicationId, _backend);
                _glamourerUnlock.Invoke(chara.ObjectIndex, LockCode);
                _glamourerRevert.Invoke(chara.ObjectIndex, LockCode);

                _snowMediator.Publish(new PenumbraRedrawCharacterMessage(chara));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{appid}] Error during appearance revert", applicationId);
            }
        }, token).ConfigureAwait(false);
    }

    public async Task RevertByNameAsync(ILogger logger, string name, Guid applicationId)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning)
        {
            return;
        }

        await Service.RunOnFrameworkAsync(() => RevertByName(logger, name, applicationId)).ConfigureAwait(false);
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
            _glamourerRevertByName.Invoke(name, LockCode);
            _glamourerUnlockByName.Invoke(name, LockCode);
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
}
