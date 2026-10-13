using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;
using Snowcloak.UI.Components;

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
    private bool _browserCursorLease;
    private bool _savedCursorOverride;
    private bool _savedNoMouseCursorChange;
    private bool _savedMouseDrawCursor;

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

        RegisterDynamicWindow<ProfileOpenStandaloneMessage, StandaloneProfileUi>(
            (msg, ui) => string.Equals(ui.Ident, msg.Ident ?? msg.Pair?.Ident ?? string.Empty, StringComparison.Ordinal)
                         && ui.RequestedVisibility == msg.RequestedVisibility,
            msg => _uiFactory.CreateStandaloneProfileUi(msg.UserData, msg.Pair, msg.RequestedVisibility, msg.Ident, msg.FallbackName));

        RegisterDynamicWindow<OpenSyncshellAdminPanel, SyncshellAdminUI>(
            (msg, ui) => string.Equals(ui.GroupFullInfo.GID, msg.GroupInfo.GID, StringComparison.Ordinal),
            msg => _uiFactory.CreateSyncshellAdminUi(msg.GroupInfo));

        RegisterDynamicWindow<OpenSyncshellEventsWindow, SyncshellEventsWindow>(
            (msg, ui) => string.Equals(ui.GroupFullInfo.GID, msg.GroupInfo.GID, StringComparison.Ordinal),
            msg => _uiFactory.CreateSyncshellEventsUi(msg.GroupInfo));

        RegisterDynamicWindow<OpenPermissionWindow, PermissionWindowUI>(
            (msg, ui) => msg.Pair == ui.Pair,
            msg => _uiFactory.CreatePermissionPopupUi(msg.Pair));

        RegisterDynamicWindow<OpenPairAnalysisWindow, PlayerAnalysisUI>(
            (msg, ui) => msg.Pair == ui.Pair,
            msg => _uiFactory.CreatePlayerAnalysisUi(msg.Pair));

        RegisterDynamicWindow<OpenSyncTroubleshootingWindow, SyncTroubleshootingUi>(
            (msg, ui) => msg.Pair == ui.Pair,
            msg => _uiFactory.CreateSyncTroubleshootingUi(msg.Pair));

        RegisterDynamicWindow<OpenChatPopoutMessage, ChatPopoutWindow>(
            (msg, ui) => msg.Key == ui.Key,
            msg => _uiFactory.CreateChatPopoutWindow(msg.Key));

        RegisterDynamicWindow<OpenRoomAdministrationMessage, RoomAdministrationWindow>(
            (msg, ui) => string.Equals(msg.RoomId, ui.RoomId, StringComparison.Ordinal),
            msg => _uiFactory.CreateRoomAdministrationWindow(msg.RoomId));

        RegisterDynamicWindow<OpenPairRequestConfirmationMessage, PairRequestConfirmationWindow>(
            (msg, ui) => string.Equals(msg.Ident, ui.Ident, StringComparison.Ordinal),
            msg => _uiFactory.CreatePairRequestConfirmationWindow(msg.Ident, msg.CharacterName, msg.PluginName));

        Mediator.Subscribe<RemoveWindowMessage>(this, (msg) =>
        {
            _windowSystem.RemoveWindow(msg.Window);
            _createdWindows.Remove(msg.Window);
            msg.Window.Dispose();
        });
    }
    
    private void RegisterDynamicWindow<TMessage, TWindow>(Func<TMessage, TWindow, bool> matches, Func<TMessage, TWindow> factory)
        where TMessage : MessageBase
        where TWindow : WindowMediatorSubscriberBase
    {
        Mediator.Subscribe<TMessage>(this, (msg) =>
        {
            if (!_createdWindows.OfType<TWindow>().Any(w => matches(msg, w)))
            {
                var window = factory(msg);
                _createdWindows.Add(window);
                _windowSystem.AddWindow(window);
            }
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
        RestoreBrowserCursor();

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
        var browserOwnsPointer = ProfileWebClient.BrowserOwnsPointer();
        if (browserOwnsPointer && !_browserCursorLease)
        {
            _savedCursorOverride = _uiBuilder.OverrideGameCursor;
            _savedNoMouseCursorChange = (ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0;
            _savedMouseDrawCursor = ImGui.GetIO().MouseDrawCursor;
            _browserCursorLease = true;
        }
        if (browserOwnsPointer)
        {
            // Keep Dalamud's game SetCursor filter, but stop its backend/software cursor
            // from overwriting the separate browser window's cursor each frame.
            _uiBuilder.OverrideGameCursor = true;
            ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
            ImGui.GetIO().MouseDrawCursor = false;
        }
        else RestoreBrowserCursor();
        using var theme = ModernTheme.Push(SnowcloakModernPalette.Value);

        _windowSystem.Draw();
        _fileDialogManager.Draw();
    }

    private void RestoreBrowserCursor()
    {
        if (!_browserCursorLease) return;
        _uiBuilder.OverrideGameCursor = _savedCursorOverride;
        if (!ImGui.GetCurrentContext().Equals(default(ImGuiContextPtr)))
        {
            if (_savedNoMouseCursorChange) ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
            else ImGui.GetIO().ConfigFlags &= ~ImGuiConfigFlags.NoMouseCursorChange;
            ImGui.GetIO().MouseDrawCursor = _savedMouseDrawCursor;
        }
        _browserCursorLease = false;
    }
}
