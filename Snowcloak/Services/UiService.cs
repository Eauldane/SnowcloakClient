using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;

namespace Snowcloak.Services;

public sealed class UiService : DisposableMediatorSubscriberBase
{
    private readonly List<WindowMediatorSubscriberBase> _createdWindows = [];
    private readonly IUiBuilder _uiBuilder;
    private readonly FileDialogManager _fileDialogManager;
    private readonly ILogger<UiService> _logger;
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly WindowSystem _windowSystem;
    private readonly UiFactory _uiFactory;

    public UiService(ILogger<UiService> logger, IUiBuilder uiBuilder,
        SnowcloakConfigService snowcloakConfigService, WindowSystem windowSystem,
        IEnumerable<WindowMediatorSubscriberBase> windows,
        UiFactory uiFactory, FileDialogManager fileDialogManager,
        SnowMediator snowMediator) : base(logger, snowMediator)
    {
        _logger = logger;
        _logger.LogTrace("Creating {type}", GetType().Name);
        _uiBuilder = uiBuilder;
        _snowcloakConfigService = snowcloakConfigService;
        _windowSystem = windowSystem;
        _uiFactory = uiFactory;
        _fileDialogManager = fileDialogManager;

        _uiBuilder.DisableGposeUiHide = true;
        _uiBuilder.Draw += Draw;
        _uiBuilder.OpenConfigUi += ToggleUi;
        _uiBuilder.OpenMainUi += ToggleMainUi;

        foreach (var window in windows)
        {
            _windowSystem.AddWindow(window);
        }

        Mediator.Subscribe<ProfileOpenStandaloneMessage>(this, (msg) =>
        {
            if (!_createdWindows.Exists(p => p is StandaloneProfileUi ui
                                             && string.Equals(ui.UserData.UID, msg.UserData.UID, StringComparison.Ordinal)
                                             && ui.RequestedVisibility == msg.RequestedVisibility))
            {
                var window = _uiFactory.CreateStandaloneProfileUi(msg.UserData, msg.Pair, msg.RequestedVisibility);
                _createdWindows.Add(window);
                _windowSystem.AddWindow(window);
            }
        });

        Mediator.Subscribe<OpenSyncshellAdminPanel>(this, (msg) =>
        {
            if (!_createdWindows.Exists(p => p is SyncshellAdminUI ui
                && string.Equals(ui.GroupFullInfo.GID, msg.GroupInfo.GID, StringComparison.Ordinal)))
            {
                var window = _uiFactory.CreateSyncshellAdminUi(msg.GroupInfo);
                _createdWindows.Add(window);
                _windowSystem.AddWindow(window);
            }
        });

        Mediator.Subscribe<OpenPermissionWindow>(this, (msg) =>
        {
            if (!_createdWindows.Exists(p => p is PermissionWindowUI ui
                && msg.Pair == ui.Pair))
            {
                var window = _uiFactory.CreatePermissionPopupUi(msg.Pair);
                _createdWindows.Add(window);
                _windowSystem.AddWindow(window);
            }
        });

        Mediator.Subscribe<OpenPairAnalysisWindow>(this, (msg) =>
        {
            if (!_createdWindows.Exists(p => p is PlayerAnalysisUI ui
                && msg.Pair == ui.Pair))
            {
                var window = _uiFactory.CreatePlayerAnalysisUi(msg.Pair);
                _createdWindows.Add(window);
                _windowSystem.AddWindow(window);
            }
        });

        Mediator.Subscribe<RemoveWindowMessage>(this, (msg) =>
        {
            _windowSystem.RemoveWindow(msg.Window);
            _createdWindows.Remove(msg.Window);
            msg.Window.Dispose();
        });
    }

    public void ToggleMainUi()
    {
        if (_snowcloakConfigService.Current.HasValidSetup())
            Mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
        else
            Mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
    }

    public void ToggleUi()
    {
        if (_snowcloakConfigService.Current.HasValidSetup())
            Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
        else
            Mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _logger.LogTrace("Disposing {type}", GetType().Name);

        _windowSystem.RemoveAllWindows();

        foreach (var window in _createdWindows)
        {
            window.Dispose();
        }

        _uiBuilder.Draw -= Draw;
        _uiBuilder.OpenConfigUi -= ToggleUi;
        _uiBuilder.OpenMainUi -= ToggleMainUi;
    }

    private void Draw()
    {
        _windowSystem.Draw();
        _fileDialogManager.Draw();
    }
}