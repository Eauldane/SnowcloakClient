using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;
using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.UI.Components.Account;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Numerics;

namespace Snowcloak.UI;

public partial class SettingsUi : WindowMediatorSubscriberBase, IStaticWindow
{
    private enum SettingsTab
    {
        General,
        Interface,
        Notifications,
        Performance,
        Storage,
        Transfers,
        Service,
        Chat,
        Advanced
    }

    private SettingsTab _selectedTab = SettingsTab.General;

    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly AccountRegistrationService _registerService;
    private readonly ServerRegistry _serverConfigurationManager;
    private readonly SecretKeyBackupService _secretKeyBackupService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly AdvancedSettingsPanel _advancedSettingsPanel;
    private readonly ChatSettingsPanel _chatSettingsPanel;
    private readonly GeneralSettingsPanel _generalSettingsPanel;
    private readonly InterfaceSettingsPanel _interfaceSettingsPanel;
    private readonly NotificationSettingsPanel _notificationSettingsPanel;
    private readonly PerformanceSettingsPanel _performanceSettingsPanel;
    private readonly PluginAvailabilityPanel _pluginAvailabilityPanel;
    private readonly ServiceSelectionPanel _serviceSelectionPanel;
    private readonly StorageSettingsPanel _storageSettingsPanel;
    private readonly TransferSettingsPanel _transferSettingsPanel;
    private readonly TransferOverlayUiState _transferOverlayState;
    private readonly UiFontService _fontService;
    private bool _deleteAccountPopupModalShown;
    private bool _wasOpen;

    private readonly PasswordAccountFlow _accountMigrationFlow = new();
    private readonly SecretKeyBackupFlow _secretKeyBackupFlow;
    private readonly CharacterKeyAssignmentFlow _characterKeyAssignmentFlow;
    private readonly AccountUidGenerationFlow _accountUidGenerationFlow;
    private readonly StandaloneKeyRegistrationFlow _standaloneKeyFlow;

    public SettingsUi(ILogger<SettingsUi> logger,
        UiFontService fontService, AdvancedSettingsPanel advancedSettingsPanel, ChatSettingsPanel chatSettingsPanel,
        GeneralSettingsPanel generalSettingsPanel, InterfaceSettingsPanel interfaceSettingsPanel,
        NotificationSettingsPanel notificationSettingsPanel, PerformanceSettingsPanel performanceSettingsPanel,
        PluginAvailabilityPanel pluginAvailabilityPanel, StorageSettingsPanel storageSettingsPanel,
        ServiceSelectionPanel serviceSelectionPanel, TransferSettingsPanel transferSettingsPanel,
        TransferOverlayUiState transferOverlayState, FileDialogManager fileDialogManager,
        ServerRegistry serverConfigurationManager,
        SecretKeyBackupService secretKeyBackupService,
        SnowMediator mediator, PerformanceCollectorService performanceCollector,
        ApiController apiController,
        DalamudUtilService dalamudUtilService, AccountRegistrationService registerService)
        : base(logger, mediator, "Snowcloak Settings", performanceCollector)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _secretKeyBackupService = secretKeyBackupService;
        _apiController = apiController;
        _dalamudUtilService = dalamudUtilService;
        _registerService = registerService;
        _fontService = fontService;
        _advancedSettingsPanel = advancedSettingsPanel;
        _chatSettingsPanel = chatSettingsPanel;
        _generalSettingsPanel = generalSettingsPanel;
        _interfaceSettingsPanel = interfaceSettingsPanel;
        _notificationSettingsPanel = notificationSettingsPanel;
        _performanceSettingsPanel = performanceSettingsPanel;
        _pluginAvailabilityPanel = pluginAvailabilityPanel;
        _storageSettingsPanel = storageSettingsPanel;
        _serviceSelectionPanel = serviceSelectionPanel;
        _transferSettingsPanel = transferSettingsPanel;
        _transferOverlayState = transferOverlayState;
        _fileDialogManager = fileDialogManager;
        _secretKeyBackupFlow = new SecretKeyBackupFlow(logger, secretKeyBackupService, fileDialogManager);
        _characterKeyAssignmentFlow = new CharacterKeyAssignmentFlow(logger, serverConfigurationManager, apiController);
        _accountUidGenerationFlow = new AccountUidGenerationFlow(logger, registerService, apiController);
        _standaloneKeyFlow = new StandaloneKeyRegistrationFlow(logger);
        AllowClickthrough = false;
        AllowPinning = false;

        SetScaledSizeConstraints(new Vector2(780, 520), new Vector2(1100, 2000));
        Size = new Vector2(820, 640);
        SizeCondition = ImGuiCond.FirstUseEver;

        Mediator.Subscribe<OpenSettingsUiMessage>(this, (_) => Toggle());
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => GposeEnd());
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) => LastCreatedCharacterData = msg.CharacterData);
    }
    

    private CharacterData? LastCreatedCharacterData { get; set; }
    private ApiController ApiController => _apiController;
    
    protected override void DrawInternal()
    {
        SnowcloakUi.AccentColor = ElezenColours.SnowcloakBlue;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().WindowPadding.Y - 1f * ImGuiHelpers.GlobalScale + ImGui.GetStyle().ItemSpacing.Y);

        DrawSettingsSidebar();
        ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
        DrawSettingsMainContent();
    }

    public override void OnClose()
    {
        _transferOverlayState.EditTrackerPosition = false;

        base.OnClose();
    }

    private void DrawSettingsMainContent()
    {
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactBg);
        using var contentPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16f, 12f) * ImGuiHelpers.GlobalScale);
        using (ImRaii.Child("SettingsContent", new Vector2(-1, -1), false))
        {
            using var itemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 7f * ImGuiHelpers.GlobalScale));

            _ = _pluginAvailabilityPanel.Draw();
            ImGui.Separator();

            switch (_selectedTab)
            {
                case SettingsTab.General:
                    _generalSettingsPanel.Draw();
                    break;
                case SettingsTab.Interface:
                    _interfaceSettingsPanel.Draw();
                    break;
                case SettingsTab.Notifications:
                    _notificationSettingsPanel.Draw();
                    break;
                case SettingsTab.Performance:
                    _performanceSettingsPanel.Draw();
                    break;
                case SettingsTab.Storage:
                    _storageSettingsPanel.Draw();
                    break;
                case SettingsTab.Transfers:
                    _transferSettingsPanel.Draw();
                    break;
                case SettingsTab.Service:
                    using (ImRaii.Disabled(_standaloneKeyFlow.IsRunning))
                    {
                        DrawServerConfiguration();
                    }
                    break;
                case SettingsTab.Chat:
                    _chatSettingsPanel.Draw();
                    break;
                case SettingsTab.Advanced:
                    _advancedSettingsPanel.Draw(LastCreatedCharacterData);
                    break;
            }
        }
    }

    private void GposeEnd()
    {
        IsOpen = _wasOpen;
    }

    private void GposeStart()
    {
        _wasOpen = IsOpen;
        IsOpen = false;
    }

}
