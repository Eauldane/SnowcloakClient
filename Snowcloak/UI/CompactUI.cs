using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ElezenTools.UI;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Account;
using Snowcloak.API.Dto.User;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Pairing;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.UI.Components.Account;
using Snowcloak.UI.Handlers;
using Snowcloak.UI.PairingAvailability;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Snowcloak.UI;

public partial class CompactUi : WindowMediatorSubscriberBase, IStaticWindow
{
    // selected menu for states
    private enum Menu
    {
        IndividualPairs,
        Syncshells,
        Performance,
        Frostbrand
    }

    private enum PatreonLoginFeedbackLevel
    {
        None,
        Failure,
        LoggedInNoPledge,
        Success
    }

    // currebnt selected tab and sidebar state
    private Menu _selectedMenu = Menu.IndividualPairs;
    private float _transferPartHeight;
    private float _windowContentWidth;
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _configService;
    private readonly DownloadStatusStore _statusStore;
    private readonly FileUploadManager _fileTransferManager;
    private readonly GroupPanel _groupPanel;
    private readonly PairGroupsUi _pairGroupsUi;
    private readonly PairManager _pairManager;
    private readonly PairRequestService _pairRequestService;
    private readonly PerformanceDashboardPanel _performanceDashboardPanel;
    private readonly SelectGroupForPairUi _selectGroupForPairUi;
    private readonly SelectPairForGroupUi _selectPairsForGroupUi;
    private readonly NotesStore _notesStore;
    private readonly ServerRegistry _serverManager;
    private readonly Stopwatch _timeout = new();
    private readonly CharaDataManager _charaDataManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly UiFontService _fontService;
    private readonly UidDisplayHandler _uidDisplayHandler;
    private readonly PairDisplayDecorationService _guiHookService;
    private readonly FrostbrandPanel _frostbrandPanel;
    private readonly AvailabilityDispatcher _availabilityDispatcher;
    private readonly CompactPairNotePopup _newPairNotePopup;
    private readonly CompactUiShellState _shellState = new();
    private bool _buttonState;
    private readonly TagHandler _tagHandler;
    private string _characterOrCommentFilter = string.Empty;
    private string _pairToAdd = string.Empty;
    private int _secretKeyIdx = -1;
    private readonly StandaloneKeyRegistrationFlow _characterKeyFlow;
    private readonly AccountUidGenerationFlow _accountUidFlow;
    private readonly AccountRegistrationService _registerService;
    private string _secretKey = string.Empty;
    private bool _showVanityIdModal;
    private string _vanityIdInput = string.Empty;
    private Vector3 _vanityColour = Vector3.One;
    private Vector3 _vanityGlowColour = Vector3.Zero;
    private bool _useVanityColour;
    private bool _useVanityGlowColour;
    private bool _patreonStatusLoading;
    private bool _patreonLoginInProgress;
    private PatreonStatusResult _patreonStatus = new();
    private string? _patreonLoginFeedback;
    private PatreonLoginFeedbackLevel _patreonLoginFeedbackLevel = PatreonLoginFeedbackLevel.None;
    private readonly HashSet<Guid> _dismissedAnnouncementIds = new();
    private readonly Dictionary<string, object> _selectedComboItems = new(StringComparer.Ordinal);

    public CompactUi(ILogger<CompactUi> logger, UiFontService fontService,
        SnowcloakConfigService configService, ApiController apiController, PairManager pairManager, PairRequestService pairRequestService,
        PairDisplayDecorationService guiHookService, ServerRegistry serverManager, NotesStore notesStore, TagStore tagStore, ShellConfigStore shellConfigStore, SnowMediator mediator, FileUploadManager fileTransferManager, DownloadStatusStore statusStore, UidDisplayHandler uidDisplayHandler, CharaDataManager charaDataManager,
        PerformanceCollectorService performanceCollectorService, AccountRegistrationService registerService, SyncshellBudgetService syncshellBudgetService,
        GpuMemoryBudgetService gpuMemoryBudgetService, PlayerPerformanceService playerPerformanceService,
        PlayerPerformanceConfigService playerPerformanceConfigService, PairingFilterConfigService pairingFilterConfigService,
        DalamudUtilService dalamudUtilService)
        : base(logger, mediator, "SnowcloakSync###SnowcloakSyncMainUI", performanceCollectorService)
    {
        _fontService = fontService;
        _configService = configService;
        _apiController = apiController;
        _pairManager = pairManager;
        _pairRequestService = pairRequestService;
        _serverManager = serverManager;
        _notesStore = notesStore;
        _guiHookService = guiHookService;
        _registerService = registerService;
        _fileTransferManager = fileTransferManager;
        _statusStore = statusStore;
        _uidDisplayHandler = uidDisplayHandler;
        _charaDataManager = charaDataManager;
        _dalamudUtilService = dalamudUtilService;
        _tagHandler = new TagHandler(tagStore);
        _availabilityDispatcher = new AvailabilityDispatcher(logger, _pairRequestService, _dalamudUtilService, mediator);
        _frostbrandPanel = new FrostbrandPanel(_configService, pairingFilterConfigService, _fontService,
            _dalamudUtilService, _guiHookService, _pairRequestService.AvailabilityStore,
            _availabilityDispatcher, "SettingsUi");
        _performanceDashboardPanel = new PerformanceDashboardPanel(_pairManager, playerPerformanceService, playerPerformanceConfigService, gpuMemoryBudgetService);
        _newPairNotePopup = new CompactPairNotePopup(_notesStore);
        _characterKeyFlow = new StandaloneKeyRegistrationFlow(logger);
        _accountUidFlow = new AccountUidGenerationFlow(logger, _registerService, _apiController);

        _groupPanel = new(mediator, _apiController, _dalamudUtilService, _pairManager, uidDisplayHandler, _configService, _notesStore, shellConfigStore, _charaDataManager, syncshellBudgetService);
        _selectGroupForPairUi = new(_tagHandler, uidDisplayHandler);
        _selectPairsForGroupUi = new(_tagHandler, uidDisplayHandler);
        _pairGroupsUi = new(configService, _tagHandler, apiController, _selectPairsForGroupUi);


        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
#if DEBUG
        var devTitle = $"Snowcloak Sync Dev Build ({ver.Major}.{ver.Minor}.{ver.Build})";
        WindowName = $"{devTitle}###SnowcloakSyncMainUIDev";
        Toggle();
#else
       var windowTitle = $"Snowcloak Sync {ver.Major}.{ver.Minor}.{ver.Build}";
        WindowName = $"{windowTitle}###SnowcloakSyncMainUI";
#endif
        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<OpenFrostbrandUiMessage>(this, (_) =>
        {
            IsOpen = true;
            _selectedMenu = Menu.Frostbrand;
        });
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => GposeEnd());

        Flags |= ImGuiWindowFlags.NoDocking;
        this.TitleBarButtons =
        [
            new()
            {
                Icon = FontAwesomeIcon.GlobeEurope,
                ShowTooltip = () => ImGui.SetTooltip("Discord"),
                Click = (btn) => Util.OpenLink("https://discord.gg/QhcKWGMhXd")
            },
            new()
            {
                Icon = FontAwesomeIcon.Heart,
                ShowTooltip = () => ImGui.SetTooltip("Patreon"),
                Click = (btn) => Util.OpenLink("https://patreon.com/elznmods")
            }
        ];

        SetScaledSizeConstraints(new Vector2(560, 700), new Vector2(1200, 2000));
        Size = new Vector2(860, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        
    }

    protected override void DrawInternal()
    {
        SnowcloakUi.AccentColor = ElezenColours.SnowcloakBlue;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().WindowPadding.Y - 1f * ImGuiHelpers.GlobalScale + ImGui.GetStyle().ItemSpacing.Y);

        DrawSidebar();
        ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
        DrawMainContent();
    }

    private void DrawMainContent()
    {
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactBg);
        using var contentPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16f, 10f) * ImGuiHelpers.GlobalScale);
        using (var child = ImRaii.Child("MainContent", new Vector2(-1, -1), false))
        {
            _windowContentWidth = ElezenImgui.GetWindowContentRegionWidth();

            if (!_apiController.IsCurrentVersion)
            {
                var ver = _apiController.CurrentClientVersion;
                var unsupported = "UNSUPPORTED VERSION";
                using (_fontService.UidFont.Push())
                {
                    var uidTextSize = ImGui.CalcTextSize(unsupported);
                    ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(ImGuiColors.DalamudRed, unsupported);
                }
                ElezenImgui.ColouredWrappedText($"Your Snowcloak installation is out of date, the current version is {ver.Major}.{ver.Minor}.{ver.Build}. You may not be able to sync correctly or at all until you update. Open /xlplugins and update the plugin.", ImGuiColors.DalamudRed);
            }

            DrawMainIdentityPanelBackground();
            DrawAnnouncementBanners();
            using (ImRaii.PushId("header")) DrawUIDHeader();

            if (_apiController.ServerState is ServerState.Connected)
            {
                ImGui.Separator();

                switch (_selectedMenu)
                {
                    case Menu.IndividualPairs:
                        using (ImRaii.PushId("pairlist")) DrawPairList();
                        break;
                    case Menu.Syncshells:
                        using (ImRaii.PushId("syncshells")) _transferPartHeight = _groupPanel.DrawSyncshells(_windowContentWidth);
                        break;
                    case Menu.Performance:
                        using (ImRaii.PushId("performance")) _performanceDashboardPanel.Draw();
                        break;
                    case Menu.Frostbrand:
                        using (ImRaii.PushId("frostbrand")) _frostbrandPanel.Draw();
                        break;
                }

                ImGui.Separator();
                using (ImRaii.PushId("transfers")) DrawTransfers();
                _transferPartHeight = ImGui.GetCursorPosY() - _transferPartHeight;
                using (ImRaii.PushId("group-user-popup")) _selectPairsForGroupUi.Draw(_pairManager.DirectPairs);
                using (ImRaii.PushId("grouping-popup")) _selectGroupForPairUi.Draw();
            }

            _newPairNotePopup.Draw(_pairManager, _configService.Current.OpenPopupOnAdd);
            DrawVanityIdPopup();
            
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            _shellState.PublishLayoutChange(pos, size, Mediator);
        }
    }

    private static void DrawMainIdentityPanelBackground()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var min = ImGui.GetCursorScreenPos() - new Vector2(ImGui.GetStyle().WindowPadding.X, 0f);
        var max = min + new Vector2(ImGui.GetContentRegionAvail().X + ImGui.GetStyle().WindowPadding.X * 2f, 86f * scale);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(new Vector4(0.020f, 0.060f, 0.090f, 0.72f)), 0f);
        drawList.AddLine(min with { Y = max.Y }, max, Colour.Vector4ToColour(SnowcloakColours.CompactBorderSubtle), 1f * scale);
    }

    public override void OnClose()
    {
        _pairRowCache.Clear();
        base.OnClose();
    }

    private void GposeEnd()
    {
        IsOpen = _shellState.RestoreCutsceneOpenState();
    }

    private void GposeStart()
    {
        _shellState.CaptureCutsceneOpenState(IsOpen);
        IsOpen = false;
    }

    private void DrawCombo<T>(string comboName, IEnumerable<T> comboItems, Func<T, string> toName,
        Action<T?>? onSelected = null, T? initialSelectedItem = default)
    {
        var items = comboItems as IReadOnlyList<T> ?? comboItems.ToArray();
        _ = ElezenImgui.DrawCombo(comboName, items, toName, _selectedComboItems, onSelected, initialSelectedItem);
    }

}
