using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.API.Dto.User;
using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Handlers;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using MareSynchronos.Services.CharaData;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.UI.Components;
using MareSynchronos.UI.Handlers;
using MareSynchronos.WebAPI;
using MareSynchronos.WebAPI.Files;
using MareSynchronos.WebAPI.Files.Models;
using MareSynchronos.WebAPI.SignalR.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using MareSynchronos.API.Dto.Account;
using MareSynchronos.MareConfiguration.Models;

namespace MareSynchronos.UI;

public class CompactUi : WindowMediatorSubscriberBase
{
    // selected menu for states
    private enum Menu
    {
        IndividualPairs,
        Syncshells
    }

    // currebnt selected tab and sidebar state
    private Menu _selectedMenu = Menu.IndividualPairs;
    private bool _sidebarCollapsed = false;

    public float TransferPartHeight;
    public float WindowContentWidth;
    private readonly ApiController _apiController;
    private readonly MareConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, Dictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly FileUploadManager _fileTransferManager;
    private readonly GroupPanel _groupPanel;
    private readonly PairGroupsUi _pairGroupsUi;
    private readonly PairManager _pairManager;
    private readonly SelectGroupForPairUi _selectGroupForPairUi;
    private readonly SelectPairForGroupUi _selectPairsForGroupUi;
    private readonly ServerConfigurationManager _serverManager;
    private readonly Stopwatch _timeout = new();
    private readonly CharaDataManager _charaDataManager;
    private readonly UidDisplayHandler _uidDisplayHandler;
    private readonly UiSharedService _uiSharedService;
    private bool _buttonState;
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



    public CompactUi(ILogger<CompactUi> logger, UiSharedService uiShared, MareConfigService configService, ApiController apiController, PairManager pairManager, ChatService chatService,
        ServerConfigurationManager serverManager, MareMediator mediator, FileUploadManager fileTransferManager, UidDisplayHandler uidDisplayHandler, CharaDataManager charaDataManager,
        PerformanceCollectorService performanceCollectorService, AccountRegistrationService registerService)
        : base(logger, mediator, "###SnowcloakSyncMainUI", performanceCollectorService)
    {
        _uiSharedService = uiShared;
        _configService = configService;
        _apiController = apiController;
        _pairManager = pairManager;
        _serverManager = serverManager;
        _registerService = registerService;
        _fileTransferManager = fileTransferManager;
        _uidDisplayHandler = uidDisplayHandler;
        _charaDataManager = charaDataManager;
        var tagHandler = new TagHandler(_serverManager);

        _groupPanel = new(this, uiShared, _pairManager, chatService, uidDisplayHandler, _configService, _serverManager, _charaDataManager);
        _selectGroupForPairUi = new(tagHandler, uidDisplayHandler, _uiSharedService);
        _selectPairsForGroupUi = new(tagHandler, uidDisplayHandler);
        _pairGroupsUi = new(configService, tagHandler, uidDisplayHandler, apiController, _selectPairsForGroupUi, _uiSharedService);

#if DEBUG
        string dev = "Dev Build";
        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
        WindowName = $"Snowcloak Sync {dev} ({ver.Major}.{ver.Minor}.{ver.Build})###SnowcloakSyncMainUIDev";
        Toggle();
#else
        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
        WindowName = "Snowcloak Sync " + ver.Major + "." + ver.Minor + "." + ver.Build + "###SnowcloakSyncMainUI";
#endif
        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => UiSharedService_GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => UiSharedService_GposeEnd());
        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));

        //Sneaking this in here cause i want it.
        if (!_configService.Current.CompactUiAllowDocking)
        {
            Flags |= ImGuiWindowFlags.NoDocking;
        }

        //Leaving condition in for now with overwrite defaulting to off since it could cause issues i havent experienced yet
        if (_configService.Current.CompactUiSizeOverwrite)
        {
            SizeConstraints = new WindowSizeConstraints()
            {
                MinimumSize = new Vector2(_configService.Current.CompactUiMinWidth, _configService.Current.CompactUiMinHeight),
                MaximumSize = new Vector2(_configService.Current.CompactUiMaxWidth, _configService.Current.CompactUiMaxHeight),
            };
        }
        else
        {
            SizeConstraints = new WindowSizeConstraints()
            {
                MinimumSize = new Vector2(350, 400),
                MaximumSize = new Vector2(600, 2000),
            };
        }
    }

    protected override void DrawInternal()
    {
        if (_serverManager.CurrentApiUrl.Equals(ApiController.SnowcloakServiceUri, StringComparison.Ordinal))
            UiSharedService.AccentColor = SnowcloakSync.Utils.Colours._snowcloakOnline;
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
            if (_uiSharedService.IconTextButton(icon, label, (165 - ImGui.GetStyle().WindowPadding.X * 2) * ImGuiHelpers.GlobalScale))
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
            if (_uiSharedService.IconTextButton(icon, label, (165 - ImGui.GetStyle().WindowPadding.X * 2) * ImGuiHelpers.GlobalScale))
            {
                onClick();
            }
        }
    }
    private void DrawSidebar()
    {
        // Adjust both values below to change size, 40 seems good to fit the buttons
        // 150 seems decent enough to fit the text into it, could be smaller
        // Elf note: Adjusted to 165 since "Character Analysis" hung off the end a bit
        var sidebarWidth = (_sidebarCollapsed ? 40 : 165) * ImGuiHelpers.GlobalScale;

        using (var child = ImRaii.Child("Sidebar", new Vector2(sidebarWidth, -1), true))
        {
            var collapseIcon = _sidebarCollapsed ? FontAwesomeIcon.ArrowRight : FontAwesomeIcon.ArrowLeft;
            if (_uiSharedService.IconButton(collapseIcon))
            {
                _sidebarCollapsed = !_sidebarCollapsed;
            }
            UiSharedService.AttachToolTip(_sidebarCollapsed ? "Expand Sidebar" : "Collapse Sidebar");

            ImGui.Separator();

            // Buttons with state change
            DrawSidebarButton(Menu.IndividualPairs, FontAwesomeIcon.User, "Individual Pairs");
            DrawSidebarButton(Menu.Syncshells, FontAwesomeIcon.UserFriends, "Syncshells");
            ImGui.Separator();
            //buttons without state change
            DrawSidebarAction(FontAwesomeIcon.PersonCircleQuestion, "Character Analysis",
                       () => Mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi))));
            //Abbrivated because Character Data Hub is too long and loogs ugly in the lables
            DrawSidebarAction(FontAwesomeIcon.Running, "Character Hub",
                () => Mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi))));
            DrawSidebarAction(FontAwesomeIcon.Cog, "Settings",
                () => Mediator.Publish(new UiToggleMessage(typeof(SettingsUi))));
            if (_apiController.ServerState is ServerState.Connected)
            {
                ImGui.Separator();
                DrawSidebarAction(FontAwesomeIcon.UserCircle, "Edit Profile",
                    () => Mediator.Publish(new UiToggleMessage(typeof(EditProfileUi))));
            }

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
                DrawSidebarAction(FontAwesomeIcon.Users, $"{userCount} Users Online", () => { });
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
            var color = UiSharedService.GetBoolColor(!_serverManager.CurrentServer!.FullPause);

            if (_apiController.ServerState is not (ServerState.Reconnecting or ServerState.Disconnecting))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, color);

                DrawSidebarAction(connectedIcon, !_serverManager.CurrentServer.FullPause ? "Disconnect": "Connect",
                () =>
                {
                    _serverManager.CurrentServer.FullPause = !_serverManager.CurrentServer.FullPause;
                    _serverManager.Save();
                    _ = _apiController.CreateConnections();
                });
                ImGui.PopStyleColor();
                UiSharedService.AttachToolTip(!_serverManager.CurrentServer.FullPause ? "Disconnect from " + _serverManager.CurrentServer.ServerName : "Connect to " + _serverManager.CurrentServer.ServerName);
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
                UiSharedService.ColorTextWrapped($"Your Snowcloak installation is out of date, the current version is {ver.Major}.{ver.Minor}.{ver.Build}. " +
                    $"It is highly recommended to keep Snowcloak up to date. Open /xlplugins and update the plugin.", ImGuiColors.DalamudRed);
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

            if (ImGui.BeginPopupModal("Set Notes for New User", ref _showModalForUserAddition, UiSharedService.PopupWindowFlags))
            {
                if (_lastAddedUser == null)
                {
                    _showModalForUserAddition = false;
                }
                else
                {
                    UiSharedService.TextWrapped($"You have successfully added {_lastAddedUser.UserData.AliasOrUID}. Set a local note for the user in the field below:");
                    ImGui.InputTextWithHint("##noteforuser", $"Note for {_lastAddedUser.UserData.AliasOrUID}", ref _lastAddedUserComment, 100);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Save Note"))
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
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, "Log in with XIVAuth"))
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
                                _registrationMessage = "An unknown error occured. Please try again later.";
                            return;
                        }
                        _registrationMessage = "Account registered. Welcome to Snowcloak!";
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

            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Plus, "Add character with existing key"))
            {
                _serverManager.CurrentServer!.Authentications.Add(new MareConfiguration.Models.Authentication()
                {
                    CharacterName = _uiSharedService.PlayerName,
                    WorldId = _uiSharedService.WorldId,
                    SecretKeyIdx = _secretKeyIdx
                });

                _serverManager.Save();

                _ = _apiController.CreateConnections();
            }
            ImGui.EndDisabled(); // _registrationInProgress || _registrationSuccess

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
                UiSharedService.ColorTextWrapped("Your secret key must be exactly 64 characters long.", ImGuiColors.DalamudRed);
            }
            else if (_secretKey.Length == 64)
            {
                using var saveDisabled = ImRaii.Disabled(_uiSharedService.ApiController.ServerState == ServerState.Connecting || _uiSharedService.ApiController.ServerState == ServerState.Reconnecting);
                if (ImGui.Button("Save and Connect"))
                {
                    string keyName;
                    if (_serverManager.CurrentServer == null) _serverManager.SelectServer(0);
                    if (_registrationReply != null && _secretKey.Equals(_registrationReply.SecretKey, StringComparison.Ordinal))
                        keyName = _registrationReply.UID + $" (registered {DateTime.Now:yyyy-MM-dd})";
                    else
                        keyName = $"Secret Key added on Setup ({DateTime.Now:yyyy-MM-dd})";
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
            UiSharedService.ColorTextWrapped("No secret keys are configured for the current server.", ImGuiColors.DalamudYellow);
        }
    }

    private void DrawAddPair()
    {
        var buttonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus);
        ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth() - ImGui.GetWindowContentRegionMin().X - buttonSize.X);
        ImGui.InputTextWithHint("##otheruid", "Other players UID/Alias", ref _pairToAdd, 20);
        ImGui.SameLine(ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() - buttonSize.X);
        var canAdd = !_pairManager.DirectPairs.Any(p => string.Equals(p.UserData.UID, _pairToAdd, StringComparison.Ordinal) || string.Equals(p.UserData.Alias, _pairToAdd, StringComparison.Ordinal));
        using (ImRaii.Disabled(!canAdd))
        {
            if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
            {
                _ = _apiController.UserAddPair(new(new(_pairToAdd)));
                _pairToAdd = string.Empty;
            }
            UiSharedService.AttachToolTip("Pair with " + (_pairToAdd.IsNullOrEmpty() ? "other user" : _pairToAdd));
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
                UiSharedService.AttachToolTip($"Hold Control to {(button == FontAwesomeIcon.Play ? "resume" : "pause")} pairing with {users.Count} out of {userCount} displayed users.");
            else
                UiSharedService.AttachToolTip($"Next execution is available at {(5000 - _timeout.ElapsedMilliseconds) / 1000} seconds");
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
        var users = GetFilteredUsers().OrderBy(u => u.GetPairSortKey(), StringComparer.Ordinal);

        var onlineUsers = users.Where(u => u.UserPair!.OtherPermissions.IsPaired() && (u.IsOnline && !u.IsVisible && (!u.UserPair!.OtherPermissions.IsPaused() && !u.UserPair!.OwnPermissions.IsPaused()))).Select(c => new DrawUserPair("Online" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager)).ToList();
        var pausedUsers = users.Where(u => u.UserPair!.OtherPermissions.IsPaired() && (u.UserPair!.OtherPermissions.IsPaused() || u.UserPair!.OwnPermissions.IsPaused())).Select(c => new DrawUserPair("Paused" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager)).ToList();
        var visibleUsers = users.Where(u => u.IsVisible).Select(c => new DrawUserPair("Visible" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager)).ToList();
        var offlineUsers = users.Where(u => !u.UserPair!.OtherPermissions.IsPaired() || !u.IsOnline && (!u.UserPair!.OwnPermissions.IsPaused() && !u.UserPair.OtherPermissions.IsPaused())).Select(c => new DrawUserPair("Offline" + c.UserData.UID, c, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi, _uiSharedService, _charaDataManager)).ToList();

        ImGui.BeginChild("list", new Vector2(WindowContentWidth, ySize), border: false);

        _pairGroupsUi.Draw(visibleUsers, onlineUsers, pausedUsers, offlineUsers);

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
            UiSharedService.AttachToolTip("Click to copy");

            if (!string.Equals(_apiController.DisplayName, _apiController.UID, StringComparison.Ordinal))
            {
                var origTextSize = ImGui.CalcTextSize(_apiController.UID);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (origTextSize.X / 2));
                ImGui.TextColored(GetUidColor(), _apiController.UID);
                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText(_apiController.UID);
                }
                UiSharedService.AttachToolTip("Click to copy");
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
        return _pairManager.DirectPairs.Where(p =>
        {
            if (_characterOrCommentFilter.IsNullOrEmpty()) return true;
            return p.UserData.AliasOrUID.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ||
                   (p.GetNote()?.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (p.PlayerName?.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ?? false);
        }).ToList();
    }

    private string GetServerError()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => "Attempting to connect to the server.",
            ServerState.Reconnecting => "Connection to server interrupted, attempting to reconnect to the server.",
            ServerState.Disconnected => "You are currently disconnected from the sync server.",
            ServerState.Disconnecting => "Disconnecting from the server",
            ServerState.Unauthorized => "Server Response: " + _apiController.AuthFailureMessage,
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
            uidColour = SnowcloakSync.Utils.Colours.Hex2Vector4(uidCol);
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