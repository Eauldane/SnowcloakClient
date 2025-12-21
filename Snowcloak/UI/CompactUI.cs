using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
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
using Snowcloak.Services.Localisation;

namespace Snowcloak.UI;

public class CompactUi : WindowMediatorSubscriberBase
{
    // selected menu for states
    private enum Menu
    {
        IndividualPairs,
        Syncshells,
        Frostbrand
    }

    // currebnt selected tab and sidebar state
    private Menu _selectedMenu = Menu.IndividualPairs;
    private bool _sidebarCollapsed = false;

    public float TransferPartHeight;
    public float WindowContentWidth;
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, Dictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly FileUploadManager _fileTransferManager;
    private readonly GroupPanel _groupPanel;
    private readonly PairGroupsUi _pairGroupsUi;
    private readonly PairManager _pairManager;
    private readonly PairRequestService _pairRequestService;
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
    private readonly LocalisationService _localisationService;
    private string _secretKey = string.Empty;



    public CompactUi(ILogger<CompactUi> logger, UiSharedService uiShared, SnowcloakConfigService configService, ApiController apiController, PairManager pairManager, PairRequestService pairRequestService, ChatService chatService,
        GuiHookService guiHookService, ServerConfigurationManager serverManager, SnowMediator mediator, FileUploadManager fileTransferManager, UidDisplayHandler uidDisplayHandler, CharaDataManager charaDataManager,
        PerformanceCollectorService performanceCollectorService, AccountRegistrationService registerService, LocalisationService localisationService)
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
        _localisationService = localisationService;
        _fileTransferManager = fileTransferManager;
        _uidDisplayHandler = uidDisplayHandler;
        _charaDataManager = charaDataManager;
        _tagHandler = new TagHandler(_serverManager);
        _pendingPairRequestSection = new PendingPairRequestSection(_pairRequestService, _serverManager, _uiSharedService, _localisationService);
        _frostbrandPanel = new FrostbrandPanel(_apiController, _configService, _pairRequestService, _uiSharedService, _guiHookService, _localisationService, _pendingPairRequestSection, "SettingsUi");
        
        _groupPanel = new(this, uiShared, _pairManager, chatService, uidDisplayHandler, _configService, _serverManager, _charaDataManager, _localisationService);
        _selectGroupForPairUi = new(_tagHandler, uidDisplayHandler, _uiSharedService, _localisationService);
        _selectPairsForGroupUi = new(_tagHandler, uidDisplayHandler, _localisationService);
        _pairGroupsUi = new(configService, _tagHandler, uidDisplayHandler, apiController, _selectPairsForGroupUi, _uiSharedService, _localisationService);


        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
#if DEBUG
        var devTitle = string.Format(L("Window.Title.Dev", "Snowcloak Sync Dev Build ({0})"), $"{ver.Major}.{ver.Minor}.{ver.Build}");
        WindowName = $"{devTitle}###SnowcloakSyncMainUIDev";
        Toggle();
#else
       var windowTitle = string.Format(L("Window.Title", "Snowcloak Sync {0}"), $"{ver.Major}.{ver.Minor}.{ver.Build}");
        WindowName = $"{windowTitle}###SnowcloakSyncMainUI";
#endif
        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = true);
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
                ShowTooltip = () => ImGui.SetTooltip(L("TitleBar.Tooltip.Discord", "Discord")),
                Click = (btn) => Util.OpenLink("https://discord.gg/snowcloak")
            },
            new()
            {
                Icon = FontAwesomeIcon.Heart,
                ShowTooltip = () => ImGui.SetTooltip(L("TitleBar.Tooltip.Patreon", "Patreon")),
                Click = (btn) => Util.OpenLink("https://patreon.com/eauldane")
            }
        ];
        // changed min size
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(600, 2000),
        };
    }
    
    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"CompactUI.{key}", fallback);
    }

    protected override void DrawInternal()
    {
        if (_serverManager.CurrentApiUrl.Equals(ApiController.SnowcloakServiceUri, StringComparison.Ordinal))
            UiSharedService.AccentColor = Colours._snowcloakOnline;
        else
            UiSharedService.AccentColor = ImGuiColors.ParsedGreen;
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
            UiSharedService.AttachToolTip(label);
        }
        else
        {
            if (_uiSharedService.IconTextButton(icon, label, (180 - ImGui.GetStyle().WindowPadding.X * 2) * ImGuiHelpers.GlobalScale))
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
            UiSharedService.AttachToolTip(label);
        }
        else
        {
            if (_uiSharedService.IconTextButton(icon, label, (180 - ImGui.GetStyle().WindowPadding.X * 2) * ImGuiHelpers.GlobalScale))
            {
                onClick();
            }
        }
    }

    private string GetFrostbrandSidebarLabel()
    {
        var label = L("Sidebar.Frostbrand", "Frostbrand");
        var pending = _pendingPairRequestSection.PendingCount;

        return pending > 0
            ? string.Format(L("Sidebar.Frostbrand.Pending", "{0} ({1})"), label, pending)
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
            UiSharedService.AttachToolTip(_sidebarCollapsed
                ? L("Sidebar.Expand", "Expand Sidebar")
                : L("Sidebar.Collapse", "Collapse Sidebar"));
            
            ImGui.Separator();

            // Buttons with state change
            DrawSidebarButton(Menu.IndividualPairs, FontAwesomeIcon.User, L("Sidebar.IndividualPairs", "Direct Pairs"));
            DrawSidebarButton(Menu.Syncshells, FontAwesomeIcon.UserFriends, L("Sidebar.Syncshells", "Syncshells"));
            if (_apiController.ServerState is ServerState.Connected)
            {
                DrawSidebarButton(Menu.Frostbrand, FontAwesomeIcon.Snowflake, GetFrostbrandSidebarLabel());
            }
            ImGui.Separator();
            //buttons without state change
            DrawSidebarAction(FontAwesomeIcon.PersonCircleQuestion, L("Sidebar.CharacterAnalysis", "Character Analysis"),
                () => Mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi))));
            //Abbrivated because Character Data Hub is too long and loogs ugly in the lables
            DrawSidebarAction(FontAwesomeIcon.Running, L("Sidebar.CharacterHub", "Character Hub"),
                () => Mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi))));
            DrawSidebarAction(FontAwesomeIcon.Cog, L("Sidebar.Settings", "Settings"),
                () => Mediator.Publish(new UiToggleMessage(typeof(SettingsUi))));
            ImGui.Separator();

            if (_apiController.ServerState is ServerState.Connected)
            {
                DrawSidebarAction(FontAwesomeIcon.UserCircle, L("Sidebar.EditProfile", "Edit Profile"),
                    () => Mediator.Publish(new UiToggleMessage(typeof(EditProfileUi))));
            }

            {
                #if DEBUG
                DrawSidebarAction(FontAwesomeIcon.UserCog, L("Sidebar.AccountManagement", "Account Management"),
                    () => Util.OpenLink("http://account.snow.cloak"));
                #else
                DrawSidebarAction(FontAwesomeIcon.UserCog, L("Sidebar.AccountManagement", "Account Management"),
                    () => Util.OpenLink("https://account.snowcloak-sync.com"));
                #endif
            }
            DrawSidebarAction(FontAwesomeIcon.UserCog, L("Sidebar.UserGuide", "User Guide"),
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
                DrawSidebarAction(FontAwesomeIcon.Users, string.Format(L("Sidebar.UsersOnline", "{0} Users Online"), userCount), () => { });
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                DrawSidebarAction(FontAwesomeIcon.ExclamationTriangle, L("Sidebar.NotConnected", "Not connected"), () => { });
                ImGui.PopStyleColor();
            }

            // restore normal button
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);
            var connectedIcon = _serverManager.CurrentServer!.FullPause ? FontAwesomeIcon.Unlink : FontAwesomeIcon.Link;
            var color = UiSharedService.GetBoolColor(!_serverManager.CurrentServer!.FullPause);

            if (_apiController.ServerState is not (ServerState.Reconnecting or ServerState.Disconnecting))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, color);

                DrawSidebarAction(connectedIcon, !_serverManager.CurrentServer.FullPause ? L("Sidebar.Disconnect", "Disconnect") : L("Sidebar.Connect", "Connect"),
                    () =>
                {
                    _serverManager.CurrentServer.FullPause = !_serverManager.CurrentServer.FullPause;
                    _serverManager.Save();
                    _ = _apiController.CreateConnections();
                });
                ImGui.PopStyleColor();
                UiSharedService.AttachToolTip(!_serverManager.CurrentServer.FullPause
                    ? string.Format(L("Sidebar.DisconnectTooltip", "Disconnect from {0}"), _serverManager.CurrentServer.ServerName)
                    : string.Format(L("Sidebar.ConnectTooltip", "Connect to {0}"), _serverManager.CurrentServer.ServerName));
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
                var unsupported = L("Version.UnsupportedTitle", "UNSUPPORTED VERSION");
                using (_uiSharedService.UidFont.Push())
                {
                    var uidTextSize = ImGui.CalcTextSize(unsupported);
                    ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(ImGuiColors.DalamudRed, unsupported);
                }
                UiSharedService.ColorTextWrapped(string.Format(L("Version.UnsupportedMessage", "Your Snowcloak installation is out of date, the current version is {0}.{1}.{2}. You may not be able to sync correctly or at all until you update. Open /xlplugins and update the plugin."), ver.Major, ver.Minor, ver.Build), ImGuiColors.DalamudRed);
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
                ImGui.OpenPopup(L("NewUserNotes.Title", "Set Notes for New User"));
                _showModalForUserAddition = true;
                _lastAddedUserComment = string.Empty;
            }

            var newUserPopupTitle = L("NewUserNotes.Title", "Set Notes for New User");

            if (ImGui.BeginPopupModal(newUserPopupTitle, ref _showModalForUserAddition, UiSharedService.PopupWindowFlags))
            {
                if (_lastAddedUser == null)
                {
                    _showModalForUserAddition = false;
                }
                else
                {
                    UiSharedService.TextWrapped(string.Format(L("NewUserNotes.Description", "You have successfully added {0}. Set a local note for the user in the field below:"), _lastAddedUser.UserData.AliasOrUID));
                    ImGui.InputTextWithHint("##noteforuser", string.Format(L("NewUserNotes.InputHint", "Note for {0}"), _lastAddedUser.UserData.AliasOrUID), ref _lastAddedUserComment, 100);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, L("NewUserNotes.Save", "Save Note")))
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
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, L("AddCharacter.LoginXIVAuth", "Log in with XIVAuth")))
            {
                _registrationInProgress = true;
                _ = Task.Run(async () => {
                    try
                    {
                        var reply = await _registerService.XIVAuth(CancellationToken.None).ConfigureAwait(false);
                        if (!reply.Success)
                        {
                            _logger.LogWarning("Registration failed: {err}", reply.ErrorMessage);
                            _registrationMessage = reply.ErrorMessage;
                            if (_registrationMessage.IsNullOrEmpty())
                                _registrationMessage = L("AddCharacter.UnknownError", "An unknown error occured. Please try again later.");
                            return;
                        }
                        _registrationMessage = L("AddCharacter.Success", "Account registered. Welcome to Snowcloak!");
                        _secretKey = reply.SecretKey ?? "";
                        _registrationReply = reply;
                        _registrationSuccess = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Registration failed");
                        _registrationSuccess = false;
                        _registrationMessage = L("AddCharacter.UnknownError", "An unknown error occured. Please try again later.");
                    }
                    finally
                    {
                        _registrationInProgress = false;
                    }
                });
            }

            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, L("AddCharacter.AddWithExistingKey", "Add character with existing key")))
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
                ImGui.TextUnformatted(L("AddCharacter.Waiting", "Waiting for the server..."));
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
                UiSharedService.ColorTextWrapped(L("AddCharacter.SecretKeyLength", "Your secret key must be exactly 64 characters long."), ImGuiColors.DalamudRed);
            }
            else if (_secretKey.Length == 64)
            {
                using var saveDisabled = ImRaii.Disabled(_uiSharedService.ApiController.ServerState == ServerState.Connecting || _uiSharedService.ApiController.ServerState == ServerState.Reconnecting);
                if (ImGui.Button(L("AddCharacter.SaveAndConnect", "Save and Connect")))
                {
                    string keyName;
                    if (_serverManager.CurrentServer == null) _serverManager.SelectServer(0);
                    if (_registrationReply != null && _secretKey.Equals(_registrationReply.SecretKey, StringComparison.Ordinal))
                        keyName = string.Format(L("AddCharacter.RegisteredKeyName", "{0} (registered {1:yyyy-MM-dd})"), _registrationReply.UID, DateTime.Now);
                    else
                        keyName = string.Format(L("AddCharacter.AddedKeyName", "Secret Key added on Setup ({0:yyyy-MM-dd})"), DateTime.Now);
                    _serverManager.CurrentServer!.SecretKeys.Add(_serverManager.CurrentServer.SecretKeys.Select(k => k.Key).LastOrDefault() + 1, new SecretKey()
                    {
                        FriendlyName = keyName,
                        Key = _secretKey,
                    });
                    _serverManager.AddCurrentCharacterToServer(save: false);
                    _ = Task.Run(() => _uiSharedService.ApiController.CreateConnections());
                }
            }
            _uiSharedService.DrawCombo(L("AddCharacter.SecretKeyLabel", "Secret Key") + "##addCharacterSecretKey", keys, (f) => f.Value.FriendlyName, (f) => _secretKeyIdx = f.Key);
        }
        else
        {
            UiSharedService.ColorTextWrapped(L("AddCharacter.NoSecretKeys", "No secret keys are configured for the current server."), ImGuiColors.DalamudYellow);
        }
        ImGui.EndDisabled(); // _registrationInProgress || _registrationSuccess

    }

    private void DrawAddPair()
    {
        var buttonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus);
        ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth() - ImGui.GetWindowContentRegionMin().X - buttonSize.X);
        ImGui.InputTextWithHint("##otheruid", L("Pairs.AddInputHint", "Other players UID/Alias"), ref _pairToAdd, 20);
        ImGui.SameLine(ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() - buttonSize.X);
        var canAdd = !_pairManager.DirectPairs.Any(p => string.Equals(p.UserData.UID, _pairToAdd, StringComparison.Ordinal) || string.Equals(p.UserData.Alias, _pairToAdd, StringComparison.Ordinal));
        using (ImRaii.Disabled(!canAdd))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
            {
                _ = _apiController.UserAddPair(new(new(_pairToAdd)));
                _pairToAdd = string.Empty;
            }
            UiSharedService.AttachToolTip(string.Format(L("Pairs.AddTooltip", "Pair with {0}"), _pairToAdd.IsNullOrEmpty() ? L("Pairs.AddTooltipFallback", "other user") : _pairToAdd));
        }

        ImGuiHelpers.ScaledDummy(2);
    }

    private void DrawFilter()
    {
        var playButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Play);

        var users = GetFilteredUsers();
        var userCount = users.Count;

        var spacing = userCount > 0
            ? playButtonSize.X + ImGui.GetStyle().ItemSpacing.X
            : 0;

        ImGui.SetNextItemWidth(WindowContentWidth - spacing);
        ImGui.InputTextWithHint("##filter", L("Pairs.FilterHint", "Filter for UID/notes"), ref _characterOrCommentFilter, 255);
        
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
                UiSharedService.AttachToolTip(string.Format(L("Pairs.FilterToggleTooltip", "Hold Control to {0} pairing with {1} out of {2} displayed users."), button == FontAwesomeIcon.Play ? L("Pairs.FilterToggleResume", "resume") : L("Pairs.FilterTogglePause", "pause"), users.Count, userCount));
            else
                UiSharedService.AttachToolTip(string.Format(L("Pairs.FilterToggleCooldown", "Next execution is available at {0} seconds"), (5000 - _timeout.ElapsedMilliseconds) / 1000));
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
        }).Select(c => new DrawUserPair("Online" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _localisationService)).ToList();
        var pausedUsers = users.Where(u =>
        {
            var pair = u.UserPair;
            var isPaired = pair?.OtherPermissions.IsPaired() ?? false;
            var otherPaused = pair?.OtherPermissions.IsPaused() ?? false;
            var ownPaused = pair?.OwnPermissions.IsPaused() ?? false;

            return isPaired && (otherPaused || ownPaused);
        }).Select(c => new DrawUserPair("Paused" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _localisationService)).ToList();
        var visibleUsers = users.Where(u => u.IsVisible).Select(c => new DrawUserPair("Visible" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _localisationService)).ToList();

        var offlineUsers = users.Where(u =>
        {
            var pair = u.UserPair;
            var isPaired = pair?.OtherPermissions.IsPaired() ?? false;
            var otherPaused = pair?.OtherPermissions.IsPaused() ?? false;
            var ownPaused = pair?.OwnPermissions.IsPaused() ?? false;

            return !isPaired || (!u.IsOnline && !(ownPaused || otherPaused));
        }).Select(c => new DrawUserPair("Offline" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager, _localisationService)).ToList();
        ImGui.BeginChild("list", new Vector2(WindowContentWidth, ySize), border: false);

        _pairGroupsUi.Draw(visibleUsers, onlineUsers, pausedUsers, offlineUsers);

        DrawPendingPairRequestsSection();
        
        ImGui.EndChild();
    }

    private void DrawTransfers()
    {
        var currentUploads = _fileTransferManager.CurrentUploads.ToList();

        if (currentUploads.Any())
        {
            ImGui.AlignTextToFramePadding();
            _uiSharedService.IconText(FontAwesomeIcon.Upload);
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
            _uiSharedService.IconText(FontAwesomeIcon.Download);
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

        using (_uiSharedService.UidFont.Push())
        {
            var uidTextSize = ImGui.CalcTextSize(uidText);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (uidTextSize.X / 2));
            ImGui.TextColored(GetUidColor(), uidText);
        }

        if (_apiController.ServerState is ServerState.Connected)
        {
            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(_apiController.DisplayName);
            }
            UiSharedService.AttachToolTip(L("Uid.Copy", "Click to copy"));
            
            if (!string.Equals(_apiController.DisplayName, _apiController.UID, StringComparison.Ordinal))
            {
                var origTextSize = ImGui.CalcTextSize(_apiController.UID);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (origTextSize.X / 2));
                ImGui.TextColored(GetUidColor(), _apiController.UID);
                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText(_apiController.UID);
                }
                UiSharedService.AttachToolTip(L("Uid.Copy", "Click to copy"));
            }
        }

        if (_apiController.ServerState is not ServerState.Connected)
        {
            UiSharedService.ColorTextWrapped(GetServerError(), GetUidColor());
            if (_apiController.ServerState is ServerState.NoSecretKey)
            {
                DrawAddCharacter();
            }
        }
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
            ServerState.Connecting => L("Status.Connecting", "Attempting to connect to the server."),
            ServerState.Reconnecting => L("Status.Reconnecting", "Connection to server interrupted, attempting to reconnect to the server."),
            ServerState.Disconnected => L("Status.Disconnected", "You are currently disconnected from the sync server."),
            ServerState.Disconnecting => L("Status.Disconnecting", "Disconnecting from the server"),
            ServerState.Unauthorized => string.Format(L("Status.Unauthorized", "Server Response: {0}"), _apiController.AuthFailureMessage),
            ServerState.Offline => L("Status.Offline", "Your selected sync server is currently offline."),
            ServerState.VersionMisMatch =>
                L("Status.VersionMismatch", "Your plugin or the server you are connecting to is out of date. Please update your plugin now. If you already did so, contact the server provider to update their server to the latest version."),
            ServerState.RateLimited => L("Status.RateLimited", "You are rate limited for (re)connecting too often. Disconnect, wait 10 minutes and try again."),
            ServerState.Connected => string.Empty,
            ServerState.NoSecretKey => L("Status.NoSecretKey", "You have no secret key set for this current character. Use the button below or open the settings and set a secret key for the current character. You can reuse the same secret key for multiple characters."),
            ServerState.MultiChara => L("Status.MultiChara", "Your Character Configuration has multiple characters configured with same name and world. You will not be able to connect until you fix this issue. Remove the duplicates from the configuration in Settings -> Service Settings -> Character Management and reconnect manually after."),
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
            uidColour = Colours.Hex2Vector4(uidCol);
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

    private string GetUidText()
    {
        return _apiController.ServerState switch
        {
            ServerState.Reconnecting => L("StatusLabel.Reconnecting", "Reconnecting"),
            ServerState.Connecting => L("StatusLabel.Connecting", "Connecting"),
            ServerState.Disconnected => L("StatusLabel.Disconnected", "Disconnected"),
            ServerState.Disconnecting => L("StatusLabel.Disconnecting", "Disconnecting"),
            ServerState.Unauthorized => L("StatusLabel.Unauthorized", "Unauthorized"),
            ServerState.VersionMisMatch => L("StatusLabel.VersionMismatch", "Version mismatch"),
            ServerState.Offline => L("StatusLabel.Offline", "Unavailable"),
            ServerState.RateLimited => L("StatusLabel.RateLimited", "Rate Limited"),
            ServerState.NoSecretKey => L("StatusLabel.NoSecretKey", "No Secret Key"),
            ServerState.MultiChara => L("StatusLabel.MultiChara", "Duplicate Characters"),
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
