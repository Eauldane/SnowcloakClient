using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.UI.Handlers;
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

public class CompactUi : WindowMediatorSubscriberBase
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
    private bool _sidebarCollapsed = false;

    public float TransferPartHeight;
    public float WindowContentWidth;
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, IReadOnlyDictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly FileUploadManager _fileTransferManager;
    private readonly GroupPanel _groupPanel;
    private readonly PairGroupsUi _pairGroupsUi;
    private readonly PairManager _pairManager;
    private readonly PairRequestService _pairRequestService;
    private readonly PerformanceDashboardPanel _performanceDashboardPanel;
    private readonly SelectGroupForPairUi _selectGroupForPairUi;
    private readonly SelectPairForGroupUi _selectPairsForGroupUi;
    private readonly ServerConfigurationManager _serverManager;
    private readonly Stopwatch _timeout = new();
    private readonly CharaDataManager _charaDataManager;
    private readonly UidDisplayHandler _uidDisplayHandler;
    private readonly UiSharedService _uiSharedService;
    private readonly GuiHookService _guiHookService;
    private readonly FrostbrandPanel _frostbrandPanel;
    private readonly PendingPairRequestSection _pendingPairRequestSection;
    private bool _buttonState;
    private readonly TagHandler _tagHandler;
    private string _characterOrCommentFilter = string.Empty;
    private Pair? _lastAddedUser;
    private string _lastAddedUserComment = string.Empty;
    private Vector2 _lastPosition = Vector2.One;
    private Vector2 _lastSize = Vector2.One;
    private string _pairToAdd = string.Empty;
    private int _secretKeyIdx = -1;
    private bool _showModalForUserAddition;
    private bool _wasOpen;
    private bool _registrationInProgress = false;
    private bool _registrationSuccess = false;
    private string? _registrationMessage;
    private RegisterReplyDto? _registrationReply;
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
    private AccountRegistrationService.PatreonStatusResult _patreonStatus = new();
    private string? _patreonLoginFeedback;
    private PatreonLoginFeedbackLevel _patreonLoginFeedbackLevel = PatreonLoginFeedbackLevel.None;

    public CompactUi(ILogger<CompactUi> logger, UiSharedService uiShared, SnowcloakConfigService configService, ApiController apiController, PairManager pairManager, PairRequestService pairRequestService, ChatService chatService,
        GuiHookService guiHookService, ServerConfigurationManager serverManager, SnowMediator mediator, FileUploadManager fileTransferManager, UidDisplayHandler uidDisplayHandler, CharaDataManager charaDataManager,
        PerformanceCollectorService performanceCollectorService, AccountRegistrationService registerService, SyncshellBudgetService syncshellBudgetService,
        GpuMemoryBudgetService gpuMemoryBudgetService, PlayerPerformanceService playerPerformanceService,
        PlayerPerformanceConfigService playerPerformanceConfigService)
        : base(logger, mediator, "SnowcloakSync###SnowcloakSyncMainUI", performanceCollectorService)
    {
        _uiSharedService = uiShared;
        _configService = configService;
        _apiController = apiController;
        _pairManager = pairManager;
        _pairRequestService = pairRequestService;
        _serverManager = serverManager;
        _guiHookService = guiHookService;
        _registerService = registerService;
        _fileTransferManager = fileTransferManager;
        _uidDisplayHandler = uidDisplayHandler;
        _charaDataManager = charaDataManager;
        _tagHandler = new TagHandler(_serverManager);
        _pendingPairRequestSection = new PendingPairRequestSection(_pairRequestService, _serverManager, _uiSharedService);
        _frostbrandPanel = new FrostbrandPanel(_configService, _pairRequestService, _uiSharedService, _guiHookService, _pendingPairRequestSection, "SettingsUi");
        _performanceDashboardPanel = new PerformanceDashboardPanel(_pairManager, playerPerformanceService, playerPerformanceConfigService, gpuMemoryBudgetService);
        
        _groupPanel = new(this, uiShared, _pairManager, chatService, uidDisplayHandler, _configService, _serverManager, _charaDataManager, syncshellBudgetService);
        _selectGroupForPairUi = new(_tagHandler, uidDisplayHandler, _uiSharedService);
        _selectPairsForGroupUi = new(_tagHandler, uidDisplayHandler);
        _pairGroupsUi = new(configService, _tagHandler, uidDisplayHandler, apiController, _selectPairsForGroupUi, _uiSharedService);


        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
#if DEBUG
        var devTitle = string.Format("Snowcloak Sync Dev Build ({0})", $"{ver.Major}.{ver.Minor}.{ver.Build}");
        WindowName = $"{devTitle}###SnowcloakSyncMainUIDev";
        Toggle();
#else
       var windowTitle = string.Format("Snowcloak Sync {0}", $"{ver.Major}.{ver.Minor}.{ver.Build}");
        WindowName = $"{windowTitle}###SnowcloakSyncMainUI";
#endif
        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<OpenFrostbrandUiMessage>(this, (_) =>
        {
            IsOpen = true;
            _selectedMenu = Menu.Frostbrand;
        });
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => UiSharedService_GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => UiSharedService_GposeEnd());
        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));
        
        Flags |= ImGuiWindowFlags.NoDocking;
        this.TitleBarButtons =
        [
            new()
            {
                Icon = FontAwesomeIcon.GlobeEurope,
                ShowTooltip = () => ImGui.SetTooltip("Discord"),
                Click = (btn) => Util.OpenLink("https://discord.gg/elznmods")
            },
            new()
            {
                Icon = FontAwesomeIcon.Heart,
                ShowTooltip = () => ImGui.SetTooltip("Patreon"),
                Click = (btn) => Util.OpenLink("https://patreon.com/elznmods")
            }
        ];
        // changed min size
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(600, 2000),
        };
    }

    protected override void DrawInternal()
    {
        UiSharedService.AccentColor = ElezenColours.SnowcloakBlue;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().WindowPadding.Y - 1f * ImGuiHelpers.GlobalScale + ImGui.GetStyle().ItemSpacing.Y);

        DrawSidebar();
        ImGui.SameLine();
        DrawMainContent();
    }

    //helper for buttons
    private void DrawSidebarButton(Menu menu, FontAwesomeIcon icon, string label)
    {
        bool isActive = _selectedMenu == menu;
        using var color = ImRaii.PushColor(ImGuiCol.Button, isActive ? UiSharedService.AccentColor : new Vector4(0, 0, 0, 0));
        using var colorHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, isActive ? UiSharedService.AccentColor : ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered]);
        using var colorActive = ImRaii.PushColor(ImGuiCol.ButtonActive, isActive ? UiSharedService.AccentColor : ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);

        if (_sidebarCollapsed)
        {
            // if the tab is collapsed state only show buttons not lable
            if (_uiSharedService.IconButton(icon))
            {
                _selectedMenu = menu;
            }
            ElezenImgui.AttachTooltip(label);
        }
        else
        {
            if (ElezenImgui.ShowIconButton(icon, label, (180 - ImGui.GetStyle().WindowPadding.X * 2) * ImGuiHelpers.GlobalScale))
            {
                _selectedMenu = menu;
            }
        }
    }
    // helper for buttons that dont cause state change
    private void DrawSidebarAction(FontAwesomeIcon icon, string label, Action onClick)
    {
        if (_sidebarCollapsed)
        {
            // if the tab is collapsed state only show buttons not lable
            if (_uiSharedService.IconButton(icon))
            {
                onClick();
            }
            ElezenImgui.AttachTooltip(label);
        }
        else
        {
            if (ElezenImgui.ShowIconButton(icon, label, (180 - ImGui.GetStyle().WindowPadding.X * 2) * ImGuiHelpers.GlobalScale))
            {
                onClick();
            }
        }
    }

    private string GetFrostbrandSidebarLabel()
    {
        var label = "Frostbrand";
        var pending = _pendingPairRequestSection.PendingCount;

        return pending > 0
            ? string.Format("{0} ({1})", label, pending)
            : label;
    }
    private void DrawSidebar()
    {
        // Adjust both values below to change size, 40 seems good to fit the buttons
        // 150 seems decent enough to fit the text into it, could be smaller
        // Elf note: Adjusted to 165 since "Character Analysis" hung off the end a bit
        // Elf note: Adjusted to 180 after that because "Account Management" is a long phrase lel
        var sidebarWidth = (_sidebarCollapsed ? 40 : 180) * ImGuiHelpers.GlobalScale;

        using (var child = ImRaii.Child("Sidebar", new Vector2(sidebarWidth, -1), true))
        {
            var collapseIcon = _sidebarCollapsed ? FontAwesomeIcon.ArrowRight : FontAwesomeIcon.ArrowLeft;
            if (_uiSharedService.IconButton(collapseIcon))
            {
                _sidebarCollapsed = !_sidebarCollapsed;
            }
            ElezenImgui.AttachTooltip(_sidebarCollapsed
                ? "Expand Sidebar"
                : "Collapse Sidebar");
            
            ImGui.Separator();

            // Buttons with state change
            DrawSidebarButton(Menu.IndividualPairs, FontAwesomeIcon.User, "Direct Pairs");
            DrawSidebarButton(Menu.Syncshells, FontAwesomeIcon.UserFriends, "Syncshells");
            if (_configService.Current.ShowCompactUiPerformanceTab)
            {
                DrawSidebarButton(Menu.Performance, FontAwesomeIcon.ChartBar, "Performance");
            }
            if (_apiController.ServerState is ServerState.Connected)
            {
                DrawSidebarButton(Menu.Frostbrand, FontAwesomeIcon.Snowflake, GetFrostbrandSidebarLabel());
            }
            ImGui.Separator();
            //buttons without state change
            DrawSidebarAction(FontAwesomeIcon.PersonCircleQuestion, "Character Analysis",
                () => Mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi))));
            //Abbrivated because Character Data Hub is too long and loogs ugly in the lables
            DrawSidebarAction(FontAwesomeIcon.Running, "Character Hub",
                () => Mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi))));
            DrawSidebarAction(FontAwesomeIcon.Cocktail, "Venues",
                () => Mediator.Publish(new UiToggleMessage(typeof(VenueAdsWindow))));
            DrawSidebarAction(FontAwesomeIcon.Comments, "Chat [BETA]",
                () => Mediator.Publish(new UiToggleMessage(typeof(ChatWindow))));
            DrawSidebarAction(FontAwesomeIcon.Cog, "Settings",
                () => Mediator.Publish(new UiToggleMessage(typeof(SettingsUi))));
            ImGui.Separator();

            if (_apiController.ServerState is ServerState.Connected)
            {
                DrawSidebarAction(FontAwesomeIcon.UserCircle, "Edit Profile",
                    () => Mediator.Publish(new UiToggleMessage(typeof(EditProfileUi))));
            }
            DrawSidebarAction(FontAwesomeIcon.UserCog, "Account Management",
                    () => Util.OpenLink("https://account.snowcloak-sync.com"));
            
            DrawSidebarAction(FontAwesomeIcon.Book, "User Guide",
                () => Util.OpenLink("https://docs.snowcloak-sync.com"));
            float bottomElementsHeight = ImGui.GetFrameHeightWithSpacing() * 2;
            var availableSpace = ImGui.GetContentRegionAvail().Y;
            if (availableSpace > bottomElementsHeight)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + availableSpace - bottomElementsHeight);

            //transparent button shenenigans
            ImGui.PushStyleColor(ImGuiCol.Button, 0);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);


            if (_apiController.ServerState is ServerState.Connected)
            {
                var userCount = _apiController.OnlineUsers.ToString(CultureInfo.InvariantCulture);
                ImGui.PushStyleColor(ImGuiCol.Text, UiSharedService.AccentColor);
                DrawSidebarAction(FontAwesomeIcon.Users, string.Format("{0} Users Online", userCount), () => { });
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                DrawSidebarAction(FontAwesomeIcon.ExclamationTriangle, "Not connected", () => { });
                ImGui.PopStyleColor();
            }

            // restore normal button
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);
            var connectedIcon = _serverManager.CurrentServer!.FullPause ? FontAwesomeIcon.Unlink : FontAwesomeIcon.Link;
            var color = ElezenImgui.GetBooleanColour(!_serverManager.CurrentServer!.FullPause);

            if (_apiController.ServerState is not (ServerState.Reconnecting or ServerState.Disconnecting))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, color);

                DrawSidebarAction(connectedIcon, !_serverManager.CurrentServer.FullPause ? "Disconnect" : "Connect",
                    () =>
                {
                    _serverManager.CurrentServer.FullPause = !_serverManager.CurrentServer.FullPause;
                    _serverManager.Save();
                    _ = _apiController.CreateConnections();
                });
                ImGui.PopStyleColor();
                ElezenImgui.AttachTooltip(!_serverManager.CurrentServer.FullPause
                    ? string.Format("Disconnect from {0}", _serverManager.CurrentServer.ServerName)
                    : string.Format("Connect to {0}", _serverManager.CurrentServer.ServerName));
            }

        }
    }

    
    private void DrawMainContent()
    {
        using (var child = ImRaii.Child("MainContent", new Vector2(-1, -1), false))
        {
            WindowContentWidth = UiSharedService.GetWindowContentRegionWidth();

            if (!_apiController.IsCurrentVersion)
            {
                var ver = _apiController.CurrentClientVersion;
                var unsupported = "UNSUPPORTED VERSION";
                using (_uiSharedService.UidFont.Push())
                {
                    var uidTextSize = ImGui.CalcTextSize(unsupported);
                    ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(ImGuiColors.DalamudRed, unsupported);
                }
                ElezenImgui.ColouredWrappedText(string.Format("Your Snowcloak installation is out of date, the current version is {0}.{1}.{2}. You may not be able to sync correctly or at all until you update. Open /xlplugins and update the plugin.", ver.Major, ver.Minor, ver.Build), ImGuiColors.DalamudRed);
            }

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
                        using (ImRaii.PushId("syncshells")) _groupPanel.DrawSyncshells();
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
                TransferPartHeight = ImGui.GetCursorPosY() - TransferPartHeight;
                using (ImRaii.PushId("group-user-popup")) _selectPairsForGroupUi.Draw(_pairManager.DirectPairs);
                using (ImRaii.PushId("grouping-popup")) _selectGroupForPairUi.Draw();
            }

            if (_configService.Current.OpenPopupOnAdd && _pairManager.LastAddedUser != null)
            {
                _lastAddedUser = _pairManager.LastAddedUser;
                _pairManager.LastAddedUser = null;
                ImGui.OpenPopup("Set Notes for New User");
                _showModalForUserAddition = true;
                _lastAddedUserComment = string.Empty;
            }

            var newUserPopupTitle = "Set Notes for New User";

            if (ImGui.BeginPopupModal(newUserPopupTitle, ref _showModalForUserAddition, UiSharedService.PopupWindowFlags))
            {
                if (_lastAddedUser == null)
                {
                    _showModalForUserAddition = false;
                }
                else
                {
                    ElezenImgui.WrappedText(string.Format("You have successfully added {0}. Set a local note for the user in the field below:", _lastAddedUser.UserData.AliasOrUID));
                    ImGui.InputTextWithHint("##noteforuser", string.Format("Note for {0}", _lastAddedUser.UserData.AliasOrUID), ref _lastAddedUserComment, 100);
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save Note"))
                    {
                        _serverManager.SetNoteForUid(_lastAddedUser.UserData.UID, _lastAddedUserComment);
                        _lastAddedUser = null;
                        _lastAddedUserComment = string.Empty;
                        _showModalForUserAddition = false;
                    }
                }
                UiSharedService.SetScaledWindowSize(275);
                ImGui.EndPopup();
            }
            DrawVanityIdPopup();
            
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            if (_lastSize != size || _lastPosition != pos)
            {
                _lastSize = size;
                _lastPosition = pos;
                Mediator.Publish(new CompactUiChange(_lastSize, _lastPosition));
            }
        }
    }

    private void DrawPendingPairRequestsSection()
    {
        if (!_configService.Current.PairingSystemEnabled)
            return;

        _pendingPairRequestSection.Draw(_tagHandler, "CompactUI", indent: true, collapsibleWhenNoTag: false);
    }

    public override void OnClose()
    {
        _uidDisplayHandler.Clear();
        base.OnClose();
    }

    private void DrawAddCharacter()
    {
        ImGui.Dummy(new(10));
        var keys = _serverManager.CurrentServer!.SecretKeys;
        ImGui.BeginDisabled(_registrationInProgress || _uiSharedService.ApiController.ServerState == ServerState.Connecting || _uiSharedService.ApiController.ServerState == ServerState.Reconnecting);
        if (keys.Any())
        {
            if (_secretKeyIdx == -1) _secretKeyIdx = keys.First().Key;
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Log in with XIVAuth"))
            {
                BeginCharacterRegistration(
                    _registerService.XIVAuth,
                    "Account registered. Welcome to Snowcloak!");
            }

            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Register new Snowcloak account"))
            {
                BeginCharacterRegistration(
                    _registerService.RegisterAccount,
                    "New account registered.\nPlease keep a copy of your secret key in case you need to reset your plugins, or to use it on another PC.");
            }

            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add character with existing key"))
            {
                _serverManager.CurrentServer!.Authentications.Add(new Configuration.Models.Authentication()
                {
                    CharacterName = _uiSharedService.PlayerName,
                    WorldId = _uiSharedService.WorldId,
                    SecretKeyIdx = _secretKeyIdx
                });

                _serverManager.Save();

                _ = _apiController.CreateConnections();
            }

            if (_registrationInProgress)
            {
                ImGui.TextUnformatted("Waiting for the server...");
            }
            else if (!_registrationMessage.IsNullOrEmpty())
            {
                if (!_registrationSuccess)
                    ImGui.TextColored(ImGuiColors.DalamudYellow, _registrationMessage);
                else
                    ImGui.TextWrapped(_registrationMessage);
            }
            if (_secretKey.Length > 0 && _secretKey.Length != 64)
            {
                ElezenImgui.ColouredWrappedText("Your secret key must be exactly 64 characters long.", ImGuiColors.DalamudRed);
            }
            else if (_secretKey.Length == 64)
            {
                using var saveDisabled = ImRaii.Disabled(_uiSharedService.ApiController.ServerState == ServerState.Connecting || _uiSharedService.ApiController.ServerState == ServerState.Reconnecting);
                if (ImGui.Button( "Save and Connect"))
                {
                    string keyName;
                    if (_serverManager.CurrentServer == null) _serverManager.SelectServer(0);
                    if (_registrationReply != null && _secretKey.Equals(_registrationReply.SecretKey, StringComparison.Ordinal))
                        keyName = string.Format("{0} (registered {1:yyyy-MM-dd})", _registrationReply.UID, DateTime.Now);
                    else
                        keyName = string.Format("Secret Key added on Setup ({0:yyyy-MM-dd})", DateTime.Now);
                    _serverManager.CurrentServer!.SecretKeys.Add(_serverManager.CurrentServer.SecretKeys.Select(k => k.Key).LastOrDefault() + 1, new SecretKey()
                    {
                        FriendlyName = keyName,
                        Key = _secretKey,
                    });
                    _serverManager.AddCurrentCharacterToServer(save: false);
                    _ = Task.Run(() => _uiSharedService.ApiController.CreateConnections());
                }
            }
            _uiSharedService.DrawCombo("Secret Key##addCharacterSecretKey", keys, (f) => f.Value.FriendlyName, (f) => _secretKeyIdx = f.Key);
        }
        else
        {
            ElezenImgui.ColouredWrappedText("No secret keys are configured for the current server.", ImGuiColors.DalamudYellow);
        }
        ImGui.EndDisabled(); // _registrationInProgress || _registrationSuccess

    }

    private void BeginCharacterRegistration(Func<CancellationToken, Task<RegisterReplyDto>> registrationFunc, string successMessage)
    {
        _registrationInProgress = true;
        _registrationMessage = null;
        _registrationSuccess = false;
        _registrationReply = null;
        _secretKey = string.Empty;

        _ = Task.Run(async () =>
        {
            try
            {
                var reply = await registrationFunc(CancellationToken.None).ConfigureAwait(false);
                if (!reply.Success)
                {
                    _logger.LogWarning("Registration failed: {err}", reply.ErrorMessage);
                    _registrationMessage = reply.ErrorMessage;
                    if (_registrationMessage.IsNullOrEmpty())
                        _registrationMessage = "An unknown error occured. Please try again later.";
                    return;
                }

                _registrationMessage = successMessage;
                _secretKey = reply.SecretKey ?? "";
                _registrationReply = reply;
                _registrationSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Registration failed");
                _registrationSuccess = false;
                _registrationMessage = "An unknown error occured. Please try again later.";
            }
            finally
            {
                _registrationInProgress = false;
            }
        });
    }

    private void DrawAddPair()
    {
        var framePadding = ImGui.GetStyle().FramePadding;
        var tallPadding = new Vector2(framePadding.X, framePadding.Y + 4f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, tallPadding);
        var buttonSize = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Plus);
        var clearButtonSize = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Times);
        var searchIconWidth = ElezenImgui.GetIconData(FontAwesomeIcon.Search).X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.AlignTextToFramePadding();
        ElezenImgui.ShowIcon(FontAwesomeIcon.Search);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth()
            - ImGui.GetWindowContentRegionMin().X
            - searchIconWidth
            - clearButtonSize.X
            - buttonSize.X
            - spacing * 3);
        ImGui.InputTextWithHint("##otheruid", "Other players UID/Alias", ref _pairToAdd, 20);
        ImGui.SameLine();
        if (_uiSharedService.IconButton(FontAwesomeIcon.Times))
        {
            _pairToAdd = string.Empty;
        }
        ElezenImgui.AttachTooltip("Clear");
        ImGui.SameLine();
        var canAdd = !_pairManager.DirectPairs.Any(p => string.Equals(p.UserData.UID, _pairToAdd, StringComparison.Ordinal) || string.Equals(p.UserData.Alias, _pairToAdd, StringComparison.Ordinal));
        using (ImRaii.Disabled(!canAdd))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
            {
                _ = _apiController.UserAddPair(new(new(_pairToAdd)));
                _pairToAdd = string.Empty;
            }
            ElezenImgui.AttachTooltip(string.Format("Send pair request to {0}", _pairToAdd.IsNullOrEmpty() ? "another player" : _pairToAdd));
        }
        ImGui.PopStyleVar();

        ImGuiHelpers.ScaledDummy(2);
    }

    private void DrawFilter()
    {
        var playButtonSize = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Play);

        var users = GetFilteredUsers();
        var userCount = users.Count;

        var spacing = userCount > 0
            ? playButtonSize.X + ImGui.GetStyle().ItemSpacing.X
            : 0;

        ImGui.SetNextItemWidth(WindowContentWidth - spacing);
        ImGui.InputTextWithHint("##filter", "Filter for UID/notes", ref _characterOrCommentFilter, 255);
        
        if (userCount == 0) return;

        var pausedUsers = users.Where(u => u.UserPair!.OwnPermissions.IsPaused() && u.UserPair.OtherPermissions.IsPaired()).ToList();
        var resumedUsers = users.Where(u => !u.UserPair!.OwnPermissions.IsPaused() && u.UserPair.OtherPermissions.IsPaired()).ToList();

        if (!pausedUsers.Any() && !resumedUsers.Any()) return;
        ImGui.SameLine();

        switch (_buttonState)
        {
            case true when !pausedUsers.Any():
                _buttonState = false;
                break;

            case false when !resumedUsers.Any():
                _buttonState = true;
                break;

            case true:
                users = pausedUsers;
                break;

            case false:
                users = resumedUsers;
                break;
        }

        if (_timeout.ElapsedMilliseconds > 5000)
            _timeout.Reset();

        var button = _buttonState ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;

        using (ImRaii.Disabled(_timeout.IsRunning))
        {
            if (_uiSharedService.IconButton(button) && UiSharedService.CtrlPressed())
            {
                foreach (var entry in users)
                {
                    var perm = entry.UserPair!.OwnPermissions;
                    perm.SetPaused(!perm.IsPaused());
                    _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, perm));
                }

                _timeout.Start();
                _buttonState = !_buttonState;
            }
            if (!_timeout.IsRunning)
                ElezenImgui.AttachTooltip(string.Format("Hold Control to {0} pairing with {1} out of {2} displayed users.", button == FontAwesomeIcon.Play ? "resume" : "pause", users.Count, userCount));
            else
                ElezenImgui.AttachTooltip(string.Format("Next execution is available at {0} seconds", (5000 - _timeout.ElapsedMilliseconds) / 1000));
        }
    }

    private void DrawPairList()
    {
        using (ImRaii.PushId("addpair")) DrawAddPair();
        using (ImRaii.PushId("pairs")) DrawPairs();
        TransferPartHeight = ImGui.GetCursorPosY();
        using (ImRaii.PushId("filter")) DrawFilter();
    }

    private void DrawPairs()
    {
        var ySize = TransferPartHeight == 0
            ? 1
            : (ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y) - TransferPartHeight - ImGui.GetCursorPosY();
        var users = GetFilteredUsers()
            .Where(u => u.UserPair != null)
            .OrderBy(u => u.GetPairSortKey(), StringComparer.Ordinal);
        
        var onlineUsers = users.Where(u =>
        {
            var pair = u.UserPair;
            var isPaired = pair?.OtherPermissions.IsPaired() ?? false;
            var otherPaused = pair?.OtherPermissions.IsPaused() ?? false;
            var ownPaused = pair?.OwnPermissions.IsPaused() ?? false;

            return isPaired && u.IsOnline && !u.IsVisible && !otherPaused && !ownPaused;
        }).Select(c => new DrawUserPair("Online" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _configService)).ToList();
        var pausedUsers = users.Where(u =>
        {
            var pair = u.UserPair;
            var isPaired = pair?.OtherPermissions.IsPaired() ?? false;
            var ownPaused = pair?.OwnPermissions.IsPaused() ?? false;

            return isPaired && ownPaused;
        }).Select(c => new DrawUserPair("Paused" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _configService)).ToList();
        var visibleUsers = users.Where(u => u.IsVisible).Select(c => new DrawUserPair("Visible" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _configService)).ToList();

        var offlineUsers = users.Where(u =>
        {
            var pair = u.UserPair;
            var isPaired = pair?.OtherPermissions.IsPaired() ?? false;
            var otherPaused = pair?.OtherPermissions.IsPaused() ?? false;
            var ownPaused = pair?.OwnPermissions.IsPaused() ?? false;

            return !isPaired || (!ownPaused && (!u.IsOnline || otherPaused));
        }).Select(c => new DrawUserPair("Offline" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _configService)).ToList();
        ImGui.BeginChild("list", new Vector2(WindowContentWidth, ySize), border: false);

        _pairGroupsUi.Draw(visibleUsers, onlineUsers, pausedUsers, offlineUsers);

        DrawPendingPairRequestsSection();
        
        ImGui.EndChild();
    }

    private void DrawTransfers()
    {
        var currentUploads = _fileTransferManager.GetCurrentUploadsSnapshot();

        if (currentUploads.Any())
        {
            ImGui.AlignTextToFramePadding();
            ElezenImgui.ShowIcon(FontAwesomeIcon.Upload);
            ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);

            var totalUploads = currentUploads.Count;

            var doneUploads = currentUploads.Count(c => c.IsTransferred);
            var totalUploaded = currentUploads.Sum(c => c.Transferred);
            var totalToUpload = currentUploads.Sum(c => c.Total);

            ImGui.TextUnformatted($"{doneUploads}/{totalUploads}");
            var uploadText = $"({UiSharedService.ByteToString(totalUploaded)}/{UiSharedService.ByteToString(totalToUpload)})";
            var textSize = ImGui.CalcTextSize(uploadText);
            ImGui.SameLine(WindowContentWidth - textSize.X);
            ImGui.TextUnformatted(uploadText);
        }

        var currentDownloads = _currentDownloads.SelectMany(d => d.Value.Values).ToList();

        if (currentDownloads.Any())
        {
            ImGui.AlignTextToFramePadding();
            ElezenImgui.ShowIcon(FontAwesomeIcon.Download);
            ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);

            var totalDownloads = currentDownloads.Sum(c => c.TotalFiles);
            var doneDownloads = currentDownloads.Sum(c => c.TransferredFiles);
            var totalDownloaded = currentDownloads.Sum(c => c.TransferredBytes);
            var totalToDownload = currentDownloads.Sum(c => c.TotalBytes);

            ImGui.TextUnformatted($"{doneDownloads}/{totalDownloads}");
            var downloadText =
                $"({UiSharedService.ByteToString(totalDownloaded)}/{UiSharedService.ByteToString(totalToDownload)})";
            var textSize = ImGui.CalcTextSize(downloadText);
            ImGui.SameLine(WindowContentWidth - textSize.X);
            ImGui.TextUnformatted(downloadText);
        }
    }

    private void DrawUIDHeader()
    {
        var uidText = GetUidText();
        var headerStart = ImGui.GetCursorPos();
        var uidColour = GetUidColor();
        var uidGlowColour = GetUidGlowColor();
        
        using (_uiSharedService.UidFont.Push())
        {
            var uidTextSize = ImGui.CalcTextSize(uidText);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (uidTextSize.X / 2));
            DrawTextWithOptionalColor(uidText, uidColour, uidGlowColour);
        }

        if (_apiController.ServerState is ServerState.Connected)
        {
            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(_apiController.DisplayName);
            }
            ElezenImgui.AttachTooltip("Click to copy");
            
                        
            if (!string.Equals(_apiController.DisplayName, _apiController.UID, StringComparison.Ordinal))
            {
                var origTextSize = ImGui.CalcTextSize(_apiController.UID);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (origTextSize.X / 2));
                DrawTextWithOptionalColor(_apiController.UID, uidColour, uidGlowColour);
                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText(_apiController.UID);
                }
                ElezenImgui.AttachTooltip("Click to copy");
            }
            
            var headerEnd = ImGui.GetCursorPos();
            var buttonHeight = ImGui.GetFrameHeight();
            var iconSize = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Pen);
            var buttonWidth = iconSize.X + ImGui.GetStyle().FramePadding.X * 2f;
            var buttonX = ImGui.GetWindowContentRegionMax().X - buttonWidth;
            var buttonY = headerStart.Y + ((headerEnd.Y - headerStart.Y) - buttonHeight) / 2f;
            ImGui.SetCursorPos(new Vector2(buttonX, buttonY));
            using (ImRaii.PushId("vanity-id-edit"))
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.Pen))
                {
                    _vanityIdInput = _apiController.VanityId ?? string.Empty;
                    _patreonLoginFeedback = null;
                    _patreonLoginFeedbackLevel = PatreonLoginFeedbackLevel.None;
                    if (_apiController.HexAllowed)
                    {
                        _useVanityColour = !string.IsNullOrWhiteSpace(_apiController.DisplayColour)
                                           || !string.IsNullOrWhiteSpace(_apiController.DisplayGlowColour);
                        _vanityColour = ParseHexColourOrDefault(_apiController.DisplayColour, Vector3.One);
                        _useVanityGlowColour = !string.IsNullOrWhiteSpace(_apiController.DisplayGlowColour);
                        _vanityGlowColour = ParseHexColourOrDefault(_apiController.DisplayGlowColour, Vector3.Zero);
                    }
                    _showVanityIdModal = true;
                    RefreshPatreonStatus();
                }
            }
            ElezenImgui.AttachTooltip("Edit vanity ID");
            ImGui.SetCursorPos(headerEnd);
        }

        if (_apiController.ServerState is not ServerState.Connected)
        {
            ElezenImgui.ColouredWrappedText(GetServerError(), GetUidColor());
            if (_apiController.ServerState is ServerState.NoSecretKey)
            {
                DrawAddCharacter();
            }
        }
    }

    private void DrawVanityIdPopup()
    {
        var popupTitle = "Edit Vanity ID";
        if (_showVanityIdModal && !ImGui.IsPopupOpen(popupTitle))
        {
            ImGui.OpenPopup(popupTitle);
        }
        if (ImGui.BeginPopupModal(popupTitle, ref _showVanityIdModal, UiSharedService.PopupWindowFlags))
        {
            ElezenImgui.WrappedText("Set your vanity ID (3-25 characters, letters/numbers/underscores/hyphens). Leave blank to clear.");
            ImGui.InputTextWithHint("##vanity-id", "Enter vanity ID (optional)", ref _vanityIdInput, 25);

            var canUseVanityColours = _apiController.HexAllowed || _patreonStatus.HasBenefits;

            ImGui.Spacing();
            if (_patreonStatusLoading)
            {
                ImGui.TextUnformatted("Checking Patreon status...");
            }
            else if (!_patreonStatus.HasBenefits)
            {
                ElezenImgui.WrappedText("Patreon subscribers get vanity colours! If you have a pledge, log in and get them.");
                using (ImRaii.Disabled(_patreonLoginInProgress))
                {
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Heart, "Log in with Patreon"))
                    {
                        StartPatreonLogin();
                    }
                }

                if (_patreonLoginInProgress)
                {
                    ImGui.TextUnformatted("Waiting for Patreon login...");
                }
                else if (!_patreonStatus.Success && !string.IsNullOrWhiteSpace(_patreonStatus.ErrorMessage))
                {
                    ImGui.TextColored(ImGuiColors.DalamudYellow, _patreonStatus.ErrorMessage);
                }
            }
            else
            {
                ElezenImgui.ColouredWrappedText("Donator benefits are active! You can set a custom display colour and glow.", ElezenColours.BooleanGreen);
            }

            if (!string.IsNullOrWhiteSpace(_patreonLoginFeedback))
            {
                ImGui.Spacing();
                var feedbackColor = _patreonLoginFeedbackLevel switch
                {
                    PatreonLoginFeedbackLevel.Failure => ImGuiColors.DalamudRed,
                    PatreonLoginFeedbackLevel.LoggedInNoPledge => ImGuiColors.DalamudYellow,
                    PatreonLoginFeedbackLevel.Success => ImGuiColors.HealerGreen,
                    _ => ImGui.GetStyle().Colors[(int)ImGuiCol.Text]
                };
                ElezenImgui.ColouredWrappedText(_patreonLoginFeedback, feedbackColor);
            }

            if (canUseVanityColours)
            {
                ImGui.Spacing();
                ElezenImgui.WrappedText("Optional: set a custom display color and glow.");
                ImGui.Checkbox("Use custom color", ref _useVanityColour);
                using (ImRaii.Disabled(!_useVanityColour))
                {
                    ImGui.ColorEdit3("Name color##vanity-color", ref _vanityColour, ImGuiColorEditFlags.NoInputs);
                    ImGui.Checkbox("Use custom glow", ref _useVanityGlowColour);
                    using (ImRaii.Disabled(!_useVanityGlowColour))
                    {
                        ImGui.ColorEdit3("Glow color##vanity-glow-color", ref _vanityGlowColour, ImGuiColorEditFlags.NoInputs);
                    }
                }
            }
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save"))
            {
                var trimmed = _vanityIdInput.Trim();
                var vanityId = string.IsNullOrEmpty(trimmed) ? null : trimmed;
                string? hexString = null;
                string? glowHexString = null;
                if (canUseVanityColours)
                {
                    if (_useVanityColour)
                    {
                        hexString = ColourVectorToHex(_vanityColour);
                        glowHexString = _useVanityGlowColour ? ColourVectorToHex(_vanityGlowColour) : string.Empty;
                    }
                    else
                    {
                        hexString = string.Empty;
                        glowHexString = string.Empty;
                    }
                }
                _ = _apiController.UserSetVanityId(new UserVanityIdDto(vanityId) { HexString = hexString, GlowHexString = glowHexString });
                _showVanityIdModal = false;
            }

            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Times, "Cancel"))
            {
                _showVanityIdModal = false;
            }
            UiSharedService.SetScaledWindowSize(360);
            ImGui.EndPopup();
        }
    }

    private void RefreshPatreonStatus()
    {
        _patreonStatusLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                _patreonStatus = await _registerService.GetPatreonStatus(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh Patreon status");
                _patreonStatus = new AccountRegistrationService.PatreonStatusResult
                {
                    Success = false,
                    ErrorMessage = "Unable to check Patreon status right now."
                };
            }
            finally
            {
                _patreonStatusLoading = false;
            }
        });
    }

    private void StartPatreonLogin()
    {
        _patreonLoginInProgress = true;
        _patreonLoginFeedback = null;
        _patreonLoginFeedbackLevel = PatreonLoginFeedbackLevel.None;
        _ = Task.Run(async () =>
        {
            try
            {
                var loginResult = await _registerService.LoginWithPatreon(CancellationToken.None).ConfigureAwait(false);
                _patreonLoginFeedback = BuildPatreonLoginFeedback(loginResult);
                _patreonLoginFeedbackLevel = GetPatreonLoginFeedbackLevel(loginResult);

                if (loginResult.Success)
                {
                    try
                    {
                        await _apiController.GetConnectionDto(publishConnected: false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to refresh connection dto after Patreon login");
                    }

                    _patreonStatus = await _registerService.GetPatreonStatus(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Patreon login flow failed");
                _patreonLoginFeedback = "Patreon login failed. Please try again.";
                _patreonLoginFeedbackLevel = PatreonLoginFeedbackLevel.Failure;
            }
            finally
            {
                _patreonLoginInProgress = false;
            }
        });
    }

    private static PatreonLoginFeedbackLevel GetPatreonLoginFeedbackLevel(AccountRegistrationService.PatreonLoginResult result)
    {
        if (!result.Success)
        {
            return PatreonLoginFeedbackLevel.Failure;
        }

        return (result.IsPayingPatron || result.IsCreatorForCampaign)
            ? PatreonLoginFeedbackLevel.Success
            : PatreonLoginFeedbackLevel.LoggedInNoPledge;
    }

    private static string BuildPatreonLoginFeedback(AccountRegistrationService.PatreonLoginResult result)
    {
        if (!result.Success)
        {
            return string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Patreon login failed. Please try again."
                : result.ErrorMessage;
        }

        if (result.IsCreatorForCampaign)
        {
            return "Login succeeded! Creator account detected for the configured campaign. This account is treated as subscribed and perks are active.";
        }

        if (result.IsPayingPatron)
        {
            return "Login succeeded! Your Patreon perks are now active.";
        }

        if (result.IsCompetitionWinner)
        {
            return "Login succeeded! No active paid membership detected, but you won one of our competitions! Winner status keeps your benefits active permanently.";
        }

        if (result.IsTestOverride)
        {
            return "Login succeeded! No active paid membership detected. Test override is active for this account.";
        }

        return "Login succeeded! No active subscription was detected for your account. Log in again once you have one to unlock vanity colours!";
    }
    
    private static Vector3 ParseHexColourOrDefault(string? hex, Vector3 fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex) && hex.Length == 6
                                            && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            var colour = ElezenTools.UI.Colour.HexToVector4(hex);
            return new Vector3(colour.X, colour.Y, colour.Z);
        }

        return fallback;
    }

    private static string ColourVectorToHex(Vector3 colour)
    {
        var r = (int)Math.Clamp(colour.X * 255f, 0f, 255f);
        var g = (int)Math.Clamp(colour.Y * 255f, 0f, 255f);
        var b = (int)Math.Clamp(colour.Z * 255f, 0f, 255f);

        return $"{r:X2}{g:X2}{b:X2}";
    }
    
    private List<Pair> GetFilteredUsers()
    {
        return _pairManager.DirectPairs
            .Where(p => p.UserPair != null)
            .Where(p =>
            {
                if (_characterOrCommentFilter.IsNullOrEmpty()) return true;
                return p.UserData.AliasOrUID.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ||
                       (p.GetNote()?.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                       (p.PlayerName?.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .ToList();
    }

    private string GetServerError()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => "Attempting to connect to the server.",
            ServerState.Reconnecting => "Connection to server interrupted, attempting to reconnect to the server.",
            ServerState.Disconnected => "You are currently disconnected from the sync server.",
            ServerState.Disconnecting => "Disconnecting from the server",
            ServerState.Unauthorized => string.Format("Server Response: {0}", _apiController.AuthFailureMessage),
            ServerState.Offline => "Your selected sync server is currently offline.",
            ServerState.VersionMisMatch =>
               "Your plugin or the server you are connecting to is out of date. Please update your plugin now. If you already did so, contact the server provider to update their server to the latest version.",
            ServerState.RateLimited => "You are rate limited for (re)connecting too often. Disconnect, wait 10 minutes and try again.",
            ServerState.Connected => string.Empty,
            ServerState.NoSecretKey => "You have no secret key set for this current character. Use the button below or open the settings and set a secret key for the current character. You can reuse the same secret key for multiple characters.",
            ServerState.MultiChara => "Your Character Configuration has multiple characters configured with same name and world. You will not be able to connect until you fix this issue. Remove the duplicates from the configuration in Settings -> Service Settings -> Character Management and reconnect manually after.",
            _ => string.Empty
        };
    }

    private Vector4 GetUidColor()
    {
        var uidCol = _apiController.DisplayColour;
        Vector4 uidColour;
        if (uidCol.IsNullOrEmpty())
        {
            uidColour = UiSharedService.AccentColor;
        } else
        {
            uidColour = ElezenTools.UI.Colour.HexToVector4(uidCol);
        }
        return _apiController.ServerState switch
        {
            
            ServerState.Connecting => ImGuiColors.DalamudYellow,
            ServerState.Reconnecting => ImGuiColors.DalamudRed,
            ServerState.Connected =>  uidColour,
            ServerState.Disconnected => ImGuiColors.DalamudYellow,
            ServerState.Disconnecting => ImGuiColors.DalamudYellow,
            ServerState.Unauthorized => ImGuiColors.DalamudRed,
            ServerState.VersionMisMatch => ImGuiColors.DalamudRed,
            ServerState.Offline => ImGuiColors.DalamudRed,
            ServerState.RateLimited => ImGuiColors.DalamudYellow,
            ServerState.NoSecretKey => ImGuiColors.DalamudYellow,
            ServerState.MultiChara => ImGuiColors.DalamudYellow,
            _ => ImGuiColors.DalamudRed
        };
    }

    private Vector4? GetUidGlowColor()
    {
        if (_apiController.ServerState is not ServerState.Connected)
        {
            return null;
        }

        return TryGetVanityColor(_apiController.DisplayGlowColour);
    }

    private static Vector4? TryGetVanityColor(string? hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor)
            || hexColor.Length != 6
            || !int.TryParse(hexColor, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return ElezenTools.UI.Colour.HexToVector4(hexColor);
    }

    private static void DrawTextWithOptionalColor(string text, Vector4? color, Vector4? glowColor = null)
    {
        if (!color.HasValue && !glowColor.HasValue)
        {
            ImGui.TextUnformatted(text);
            return;
        }

        var foreground = color ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        if (glowColor.HasValue)
        {
            var drawList = ImGui.GetWindowDrawList();
            var textPos = ImGui.GetCursorScreenPos();
            var glow = glowColor.Value;
            var glowAlpha = Math.Clamp(glow.W <= 0f ? 0.45f : glow.W, 0.05f, 1f);
            var glowU32 = ImGui.ColorConvertFloat4ToU32(new Vector4(glow.X, glow.Y, glow.Z, glowAlpha));
            var spread = 1.0f * ImGuiHelpers.GlobalScale;
            drawList.AddText(new Vector2(textPos.X - spread, textPos.Y), glowU32, text);
            drawList.AddText(new Vector2(textPos.X + spread, textPos.Y), glowU32, text);
            drawList.AddText(new Vector2(textPos.X, textPos.Y - spread), glowU32, text);
            drawList.AddText(new Vector2(textPos.X, textPos.Y + spread), glowU32, text);
        }

        ImGui.TextColored(foreground, text);
    }

    private string GetUidText()
    {
        return _apiController.ServerState switch
        {
            ServerState.Reconnecting => "Reconnecting",
            ServerState.Connecting => "Connecting",
            ServerState.Disconnected => "Disconnected",
            ServerState.Disconnecting => "Disconnecting",
            ServerState.Unauthorized => "Unauthorized",
            ServerState.VersionMisMatch => "Version mismatch",
            ServerState.Offline => "Unavailable",
            ServerState.RateLimited => "Rate Limited",
            ServerState.NoSecretKey => "No Secret Key",
            ServerState.MultiChara => "Duplicate Characters",
            ServerState.Connected => _apiController.DisplayName,
            _ => string.Empty
        };
    }

    private void UiSharedService_GposeEnd()
    {
        IsOpen = _wasOpen;
    }

    private void UiSharedService_GposeStart()
    {
        _wasOpen = IsOpen;
        IsOpen = false;
    }
}
