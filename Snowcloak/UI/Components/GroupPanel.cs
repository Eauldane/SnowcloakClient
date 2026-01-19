using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Comparer;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Handlers;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI.Components;

internal sealed class GroupPanel
{
    private readonly Dictionary<string, bool> _expandedGroupState = new(StringComparer.Ordinal);
    private readonly CompactUi _mainUi;
    private readonly PairManager _pairManager;
    private readonly ChatService _chatService;
    private readonly SnowcloakConfigService _snowcloakConfig;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly CharaDataManager _charaDataManager;
    private readonly Dictionary<string, bool> _showGidForEntry = new(StringComparer.Ordinal);
    private readonly UidDisplayHandler _uidDisplayHandler;
    private readonly UiSharedService _uiShared;
    private List<BannedGroupUserDto> _bannedUsers = new();
    private int _bulkInviteCount = 10;
    private List<string> _bulkOneTimeInvites = new();
    private string _editGroupComment = string.Empty;
    private string _editGroupEntry = string.Empty;
    private bool _errorGroupCreate = false;
    private bool _errorGroupJoin;
    private bool _isPasswordValid;
    private GroupPasswordDto? _lastCreatedGroup = null;
    private bool _modalBanListOpened;
    private bool _modalBulkOneTimeInvitesOpened;
    private bool _modalChangePwOpened;
    private string _newSyncShellPassword = string.Empty;
    private bool _showModalBanList = false;
    private bool _showModalBulkOneTimeInvites = false;
    private bool _showModalChangePassword;
    private bool _showModalCreateGroup;
    private bool _showModalEnterPassword;
    private bool _showRegionJoinError;
    private string? _pendingRegionalShellAlias;
    private string _syncShellPassword = string.Empty;
    private string _syncShellToJoin = string.Empty;
    private bool _showPublicSyncshellWarning;
    private string? _publicSyncshellAliasToJoin;

    public GroupPanel(CompactUi mainUi, UiSharedService uiShared, PairManager pairManager, ChatService chatServivce,
        UidDisplayHandler uidDisplayHandler, SnowcloakConfigService snowcloakConfig, ServerConfigurationManager serverConfigurationManager,
        CharaDataManager charaDataManager)
    {
        _mainUi = mainUi;
        _uiShared = uiShared;
        _pairManager = pairManager;
        _chatService = chatServivce;
        _uidDisplayHandler = uidDisplayHandler;
        _snowcloakConfig = snowcloakConfig;
        _serverConfigurationManager = serverConfigurationManager;
        _charaDataManager = charaDataManager;
    }

    private ApiController ApiController => _uiShared.ApiController;

    public void DrawSyncshells()
    {
        HandlePendingRegionalShell();
        using (ImRaii.PushId("addsyncshell")) DrawAddSyncshell();

        var listHeight = Math.Max(1f, ImGui.GetContentRegionAvail().Y - GetRegionJoinButtonHeight());

        using (ImRaii.PushId("syncshelllist")) DrawSyncshellList(listHeight);
        using (ImRaii.PushId("regionaljoin")) DrawRegionJoinButton();

        _mainUi.TransferPartHeight = ImGui.GetCursorPosY();
    }

    private void DrawAddSyncshell()
    {
        var buttonSize = _uiShared.GetIconButtonSize(FontAwesomeIcon.Plus);
        ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth() - ImGui.GetWindowContentRegionMin().X - buttonSize.X);
        ImGui.InputTextWithHint("##syncshellid", "Syncshell GID/Alias (leave empty to create)", ref _syncShellToJoin, 20);
        ImGui.SameLine(ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() - buttonSize.X);

        bool userCanJoinMoreGroups = _pairManager.GroupPairs.Count < ApiController.ServerInfo.MaxGroupsJoinedByUser;
        bool userCanCreateMoreGroups = _pairManager.GroupPairs.Count(u => string.Equals(u.Key.Owner.UID, ApiController.UID, StringComparison.Ordinal)) < ApiController.ServerInfo.MaxGroupsCreatedByUser;
        bool alreadyInGroup = _pairManager.GroupPairs.Select(p => p.Key).Any(p => string.Equals(p.Group.Alias, _syncShellToJoin, StringComparison.Ordinal)
            || string.Equals(p.Group.GID, _syncShellToJoin, StringComparison.Ordinal));

        if (alreadyInGroup) ImGui.BeginDisabled();
        if (_uiShared.IconButton(FontAwesomeIcon.Plus))
        {
            if (!string.IsNullOrEmpty(_syncShellToJoin))
            {
                if (userCanJoinMoreGroups)
                {
                    _errorGroupJoin = false;
                    _showModalEnterPassword = true;
                    ImGui.OpenPopup("Enter Syncshell Password");
                }
            }
            else
            {
                if (userCanCreateMoreGroups)
                {
                    _lastCreatedGroup = null;
                    _errorGroupCreate = false;
                    _showModalCreateGroup = true;
                    ImGui.OpenPopup("Create Syncshell");
                }
            }
        }
        UiSharedService.AttachToolTip(_syncShellToJoin.IsNullOrEmpty()
            ? (userCanCreateMoreGroups ? "Create Syncshell" : string.Format(CultureInfo.CurrentCulture, "You cannot create more than {0} Syncshells", ApiController.ServerInfo.MaxGroupsCreatedByUser))
            : (userCanJoinMoreGroups ? string.Format(CultureInfo.CurrentCulture, "Join Syncshell{0}", _syncShellToJoin) : string.Format(CultureInfo.CurrentCulture, "You cannot join more than {0} Syncshells", ApiController.ServerInfo.MaxGroupsJoinedByUser)));

        if (alreadyInGroup) ImGui.EndDisabled();

        var enterPasswordTitle = "Enter Syncshell Password";
        if (ImGui.BeginPopupModal(enterPasswordTitle, ref _showModalEnterPassword, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped("Joining a syncshell means you will be paired with all of its members.");
            UiSharedService.TextWrapped("You will be able to see all of their mods; and they will be able to see all of yours.");
            UiSharedService.TextWrapped("Please only proceed if you are okay with that.");
            ImGui.Separator();
            UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, "Enter the password for Syncshell {0}:", _syncShellToJoin));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##password", string.Format(CultureInfo.CurrentCulture, "{0} Password", _syncShellToJoin), ref _syncShellPassword, 255, ImGuiInputTextFlags.Password);
            if (_errorGroupJoin)
            {
                UiSharedService.ColorTextWrapped(string.Format(CultureInfo.CurrentCulture, "An error occured during joining of this Syncshell: you either have joined the maximum amount of Syncshells ({0}), it does not exist, the password you entered is wrong, you already joined the Syncshell, the Syncshell is full, or the Syncshell has closed invites.",
                        ApiController.ServerInfo.MaxGroupsJoinedByUser),
                    new Vector4(1, 0, 0, 1));
            }
            if (ImGui.Button(string.Format(CultureInfo.CurrentCulture, "Join {0}", _syncShellToJoin)))
            {
                var shell = _syncShellToJoin;
                var pw = _syncShellPassword;
                _errorGroupJoin = !ApiController.GroupJoin(new(new GroupData(shell), pw)).Result;
                if (!_errorGroupJoin)
                {
                    _syncShellToJoin = string.Empty;
                    _showModalEnterPassword = false;
                }
                _syncShellPassword = string.Empty;
            }
            UiSharedService.SetScaledWindowSize(290);
            ImGui.EndPopup();
        }

        var createSyncshellTitle = "Create Syncshell";
        if (ImGui.BeginPopupModal(createSyncshellTitle, ref _showModalCreateGroup, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped("Creating a syncshell means you are responsible for ensuring proper moderation.");
            UiSharedService.TextWrapped("Please only proceed if you are prepared to enforce community guidelines and keep your members safe.");
            UiSharedService.TextWrapped("Unmoderated synchells are not permitted. Administrator action against unmoderated shells may be performed at staff discretion.");
            UiSharedService.TextWrapped("Press the button below to create a new Syncshell.");
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            if (ImGui.Button("Create Syncshell"))
            {
                try
                {
                    _lastCreatedGroup = ApiController.GroupCreate().Result;
                }
                catch
                {
                    _lastCreatedGroup = null;
                    _errorGroupCreate = true;
                }
            }

            if (_lastCreatedGroup != null)
            {
                ImGui.Separator();
                _errorGroupCreate = false;
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "Syncshell ID: {0}", _lastCreatedGroup.Group.GID));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "Syncshell Password: {0}", _lastCreatedGroup.Password));
                ImGui.SameLine();
                if (_uiShared.IconButton(FontAwesomeIcon.Copy))
                {
                    ImGui.SetClipboardText(_lastCreatedGroup.Password);
                }
                UiSharedService.TextWrapped("You can change the Syncshell password later at any time.");
            }

            if (_errorGroupCreate)
            {
                UiSharedService.ColorTextWrapped("You are already owner of the maximum amount of Syncshells or joined the maximum amount of Syncshells. Relinquish ownership of your own Syncshells to someone else or leave existing Syncshells.",
                    new Vector4(1, 0, 0, 1));
            }

            UiSharedService.SetScaledWindowSize(350);
            ImGui.EndPopup();
        }

        ImGuiHelpers.ScaledDummy(2);
    }

    private float GetRegionJoinButtonHeight()
    {
        var style = ImGui.GetStyle();
        var height = ImGui.GetFrameHeightWithSpacing() + style.ItemSpacing.Y;

        if (_showRegionJoinError)
        {
            height += ImGui.GetTextLineHeightWithSpacing();
        }

        return height;
    }

    private void DrawRegionJoinButton()
    {
        var regionName = _uiShared.DataCenterRegion;
        var regionShell = string.IsNullOrEmpty(regionName) ? string.Empty : $"Snowcloak - {regionName} Public Syncshell";
        var regionShellJoinString = string.IsNullOrEmpty(regionName) ? string.Empty : $"{regionName} Public Syncshell";

        var isRegionShellKnown = !string.IsNullOrEmpty(regionShell);
        bool userCanJoinMoreGroups = _pairManager.GroupPairs.Count < ApiController.ServerInfo.MaxGroupsJoinedByUser;

        if (isRegionShellKnown)
        {
            var alreadyInRegionShell = _pairManager.GroupPairs.Select(p => p.Key).Any(p => string.Equals(p.Group.Alias, regionShell, StringComparison.Ordinal)
                || string.Equals(p.Group.GID, regionShell, StringComparison.Ordinal));
            using (ImRaii.Disabled(!userCanJoinMoreGroups || alreadyInRegionShell))
            {
                if (ImGui.Button($"Join {regionShellJoinString}", new Vector2(-1, 0)))
                {
                    _publicSyncshellAliasToJoin = regionShell;
                    _showRegionJoinError = false;
                    _showPublicSyncshellWarning = true;
                    ImGui.OpenPopup("Join Public Syncshell");
                }
                UiSharedService.AttachToolTip(alreadyInRegionShell
                    ? "You are already a member of your home region Syncshell."
                    : userCanJoinMoreGroups
                        ? string.Format(CultureInfo.CurrentCulture, "Join the regional Snowcloak Syncshell for {0}.", regionName)
                        : string.Format(CultureInfo.CurrentCulture, "You cannot join more than {0} Syncshells", ApiController.ServerInfo.MaxGroupsJoinedByUser));
            }
        }
        else
        {
            using (ImRaii.Disabled())
            {
                ImGui.Button("Join regional Snowcloak Syncshell", new Vector2(-1, 0));
            }
            UiSharedService.AttachToolTip("Regional Syncshell is unavailable because your datacenter region could not be determined.");
        }

        if (_showRegionJoinError)
            UiSharedService.ColorTextWrapped("The regional syncshell you're trying to join is either full and awaiting server expansion, or you don't have XIVAuth enabled. Try again later!", ImGuiColors.DalamudRed);
        
        
        DrawPublicSyncshellWarningModal();
    }

    private void DrawPublicSyncshellWarningModal()
    {
        var popupTitle = "Join Public Syncshell";
        if (ImGui.BeginPopupModal(popupTitle, ref _showPublicSyncshellWarning, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped("Public Syncshells can contain lots of people, and these people may gather in one particular spot.");
            UiSharedService.TextWrapped("This can cause significant load on both the server, and your PC. If you have a lower-end computer, we STRONGLY recommend that you do not join public syncshells.");
            UiSharedService.TextWrapped("Please note that these syncshells are not provided with the intention of facilitating large gatherings. In the event that one occurs, public syncshells will be automatically disabled and paused for all members to protect the functionality of the rest of the service.");

            ImGui.Separator();

            using (ImRaii.Disabled(string.IsNullOrEmpty(_publicSyncshellAliasToJoin)))
            {
                if (ImGui.Button("Join Public Syncshell"))
                {
                    if (!string.IsNullOrEmpty(_publicSyncshellAliasToJoin))
                    {
                        JoinRegionalSyncshell(_publicSyncshellAliasToJoin);
                    }

                    _showPublicSyncshellWarning = false;
                    _publicSyncshellAliasToJoin = null;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _showPublicSyncshellWarning = false;
                _publicSyncshellAliasToJoin = null;
            }

            UiSharedService.SetScaledWindowSize(500);
            ImGui.EndPopup();
        }
    }

    private void HandlePendingRegionalShell()
    {
        if (string.IsNullOrEmpty(_pendingRegionalShellAlias)) return;

        var hasRegionShell = _pairManager.Groups.Any(g =>
            string.Equals(g.Key.GID, _pendingRegionalShellAlias, StringComparison.Ordinal) ||
            string.Equals(g.Value.Group.Alias, _pendingRegionalShellAlias, StringComparison.Ordinal));

        if (!hasRegionShell) return;

        DisableRegionalSyncshellEffects(_pendingRegionalShellAlias);
        _pendingRegionalShellAlias = null;
    }

    private bool DisableRegionalSyncshellEffects(string regionShell)
    {
        var matchingGroup = _pairManager.Groups.FirstOrDefault(g =>
            string.Equals(g.Key.GID, regionShell, StringComparison.Ordinal) ||
            string.Equals(g.Value.Group.Alias, regionShell, StringComparison.Ordinal));

        var resolvedGid = matchingGroup.Equals(default(KeyValuePair<GroupData, GroupFullInfoDto>))
            ? regionShell
            : matchingGroup.Key.GID;

        DisableRegionalShellChat(resolvedGid);

        if (!string.Equals(resolvedGid, regionShell, StringComparison.Ordinal))
        {
            DisableRegionalShellChat(regionShell);
        }

        return !matchingGroup.Equals(default(KeyValuePair<GroupData, GroupFullInfoDto>));
    }

    private void JoinRegionalSyncshell(string regionShell)
    {
        _showRegionJoinError = false;
        var joined = ApiController.GroupJoin(new(new GroupData(regionShell), "ByTheseGlyphsOurSyncshellGuarded")).Result;
        if (joined)
        {
            var resolved = DisableRegionalSyncshellEffects(regionShell);
            if (!resolved)
            {
                _pendingRegionalShellAlias = regionShell;
            }
            else
            {
                _pendingRegionalShellAlias = null;
            }

            _syncShellToJoin = string.Empty;
        }
        else
        {
            _showRegionJoinError = true;
        }
    }        
    
    private void DisableRegionalShellChat(string gid)
    {
        var shellConfig = _serverConfigurationManager.GetShellConfigForGid(gid);
        if (shellConfig.Enabled)
        {
            shellConfig.Enabled = false;
            _serverConfigurationManager.SaveShellConfigForGid(gid, shellConfig);        }
    }
    
    private void DrawSyncshell(GroupFullInfoDto groupDto, List<Pair> pairsInGroup)
    {
        int shellNumber = _serverConfigurationManager.GetShellNumberForGid(groupDto.GID);
        var validPairsInGroup = pairsInGroup
            .Where(p => p.GroupPair.ContainsKey(groupDto))
            .ToList();

        var name = groupDto.Group.Alias ?? groupDto.GID;
        if (!_expandedGroupState.TryGetValue(groupDto.GID, out bool isExpanded))
        {
            isExpanded = false;
            _expandedGroupState.Add(groupDto.GID, isExpanded);
        }

        var icon = isExpanded ? FontAwesomeIcon.CaretSquareDown : FontAwesomeIcon.CaretSquareRight;
        _uiShared.IconText(icon);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _expandedGroupState[groupDto.GID] = !_expandedGroupState[groupDto.GID];
        }
        ImGui.SameLine();

        var textIsGid = true;
        string groupName = groupDto.GroupAliasOrGID;

        if (string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal))
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Crown.ToIconString());
            ImGui.PopFont();
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "You are the owner of Syncshell {0}", groupName));
            ImGui.SameLine();
        }
        else if (groupDto.GroupUserInfo.IsModerator())
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.UserShield.ToIconString());
            ImGui.PopFont();
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "You are a moderator of Syncshell {0}", groupName));
            ImGui.SameLine();
        }

        _showGidForEntry.TryGetValue(groupDto.GID, out var showGidInsteadOfName);
        var groupComment = _serverConfigurationManager.GetNoteForGid(groupDto.GID);
        if (!showGidInsteadOfName && !string.IsNullOrEmpty(groupComment))
        {
            groupName = groupComment;
            textIsGid = false;
        }

        if (!string.Equals(_editGroupEntry, groupDto.GID, StringComparison.Ordinal))
        {
            var shellConfig = _serverConfigurationManager.GetShellConfigForGid(groupDto.GID);
            if (!_snowcloakConfig.Current.DisableSyncshellChat && shellConfig.Enabled)
            {
                ImGui.TextUnformatted($"[{shellNumber}]");
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "Chat command prefix: /ss{0}", shellNumber));
            }
            if (textIsGid) ImGui.PushFont(UiBuilder.MonoFont);
            ImGui.SameLine();
            ImGui.TextColored(Colours.Hex2Vector4(groupDto.Group.DisplayColour), groupName);
            if (textIsGid) ImGui.PopFont();
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "Left click to switch between GID display and comment{0}Right click to change comment for {1}{0}{0}Users: {2}, Owner: {3}",
                Environment.NewLine, groupName, validPairsInGroup.Count + 1, groupDto.OwnerAliasOrUID));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                var prevState = textIsGid;
                if (_showGidForEntry.ContainsKey(groupDto.GID))
                {
                    prevState = _showGidForEntry[groupDto.GID];
                }

                _showGidForEntry[groupDto.GID] = !prevState;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _serverConfigurationManager.SetNoteForGid(_editGroupEntry, _editGroupComment);
                _editGroupComment = _serverConfigurationManager.GetNoteForGid(groupDto.GID) ?? string.Empty;
                _editGroupEntry = groupDto.GID;
                _chatService.MaybeUpdateShellName(shellNumber);
            }
        }
        else
        {
            var buttonSizes = _uiShared.GetIconButtonSize(FontAwesomeIcon.Bars).X + _uiShared.GetIconButtonSize(FontAwesomeIcon.LockOpen).X;
            ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth() - ImGui.GetCursorPosX() - buttonSizes - ImGui.GetStyle().ItemSpacing.X * 2);
            if (ImGui.InputTextWithHint("", "Comment/Notes", ref _editGroupComment, 255, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                _serverConfigurationManager.SetNoteForGid(groupDto.GID, _editGroupComment);
                _editGroupEntry = string.Empty;
                _chatService.MaybeUpdateShellName(shellNumber);
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _editGroupEntry = string.Empty;
            }
            UiSharedService.AttachToolTip("Hit ENTER to save\nRight click to cancel");
        }


        using (ImRaii.PushId(groupDto.GID + "settings")) DrawSyncShellButtons(groupDto, validPairsInGroup);
        
        if (_showModalBanList && !_modalBanListOpened)
        {
            _modalBanListOpened = true;
            ImGui.OpenPopup(string.Format(CultureInfo.CurrentCulture, "Manage Banlist for {0}", groupDto.GID));
        }

        if (!_showModalBanList) _modalBanListOpened = false;

        var banListTitle = string.Format(CultureInfo.CurrentCulture, "Manage Banlist for {0}", groupDto.GID);
        if (ImGui.BeginPopupModal(banListTitle, ref _showModalBanList, UiSharedService.PopupWindowFlags))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Retweet, "Refresh Banlist from Server"))
            {
                _bannedUsers = ApiController.GroupGetBannedUsers(groupDto).Result;
            }

            if (ImGui.BeginTable("bannedusertable" + groupDto.GID, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("UID", ImGuiTableColumnFlags.None, 1);
                ImGui.TableSetupColumn("Alias", ImGuiTableColumnFlags.None, 1);
                ImGui.TableSetupColumn("By", ImGuiTableColumnFlags.None, 1);
                ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.None, 2);
                ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.None, 3);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.None, 1);

                ImGui.TableHeadersRow();

                foreach (var bannedUser in _bannedUsers.ToList())
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(bannedUser.UID);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(bannedUser.UserAlias ?? string.Empty);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(bannedUser.BannedBy);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(bannedUser.BannedOn.ToLocalTime().ToString(CultureInfo.CurrentCulture));
                    ImGui.TableNextColumn();
                    UiSharedService.TextWrapped(bannedUser.Reason);
                    ImGui.TableNextColumn();
                    if (_uiShared.IconTextButton(FontAwesomeIcon.Check, string.Format(CultureInfo.CurrentCulture, "Unban#{0}", bannedUser.UID)))
                    {
                        _ = ApiController.GroupUnbanUser(bannedUser);
                        _bannedUsers.RemoveAll(b => string.Equals(b.UID, bannedUser.UID, StringComparison.Ordinal));
                    }
                }

                ImGui.EndTable();
            }
            UiSharedService.SetScaledWindowSize(700, 300);
            ImGui.EndPopup();
        }

        if (_showModalChangePassword && !_modalChangePwOpened)
        {
            _modalChangePwOpened = true;
            ImGui.OpenPopup("Change Syncshell Password");
        }

        if (!_showModalChangePassword) _modalChangePwOpened = false;

        var changePasswordTitle = "Change Syncshell Password";
        if (ImGui.BeginPopupModal(changePasswordTitle, ref _showModalChangePassword, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, "Enter the new Syncshell password for Syncshell {0} here.", name));
            UiSharedService.TextWrapped("This action is irreversible");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##changepw", string.Format(CultureInfo.CurrentCulture, "New password for {0}", name), ref _newSyncShellPassword, 255);
            if (ImGui.Button("Change password"))
            {
                var pw = _newSyncShellPassword;
                _isPasswordValid = ApiController.GroupChangePassword(new(groupDto.Group, pw)).Result;
                _newSyncShellPassword = string.Empty;
                if (_isPasswordValid) _showModalChangePassword = false;
            }

            if (!_isPasswordValid)
            {
                UiSharedService.ColorTextWrapped("The selected password is too short. It must be at least 10 characters.", new Vector4(1, 0, 0, 1));
            }

            UiSharedService.SetScaledWindowSize(290);
            ImGui.EndPopup();
        }

        if (_showModalBulkOneTimeInvites && !_modalBulkOneTimeInvitesOpened)
        {
            _modalBulkOneTimeInvitesOpened = true;
            ImGui.OpenPopup("Create Bulk One-Time Invites");
        }

        if (!_showModalBulkOneTimeInvites) _modalBulkOneTimeInvitesOpened = false;

        var bulkInviteTitle = "Create Bulk One-Time Invites";
        if (ImGui.BeginPopupModal(bulkInviteTitle, ref _showModalBulkOneTimeInvites, UiSharedService.PopupWindowFlags))
        {
            UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, "This allows you to create up to 100 one-time invites at once for the Syncshell {0}.{1}The invites are valid for 24h after creation and will automatically expire.",
                name, Environment.NewLine));
            ImGui.Separator();
            if (_bulkOneTimeInvites.Count == 0)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.SliderInt("Amount##bulkinvites", ref _bulkInviteCount, 1, 100);
                if (_uiShared.IconTextButton(FontAwesomeIcon.MailBulk, "Create invites"))
                {
                    _bulkOneTimeInvites = ApiController.GroupCreateTempInvite(groupDto, _bulkInviteCount).Result;
                }
            }
            else
            {
                UiSharedService.TextWrapped(string.Format(CultureInfo.CurrentCulture, "A total of {0} invites have been created.", _bulkOneTimeInvites.Count));
                if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, "Copy invites to clipboard"))
                {
                    ImGui.SetClipboardText(string.Join(Environment.NewLine, _bulkOneTimeInvites));
                }
            }

            UiSharedService.SetScaledWindowSize(290);
            ImGui.EndPopup();
        }

        bool hideOfflineUsers = validPairsInGroup.Count > 1000;
        
        ImGui.Indent(20);
        if (_expandedGroupState[groupDto.GID])
        {
            IOrderedEnumerable<Pair> sortedPairs;
            if (!_snowcloakConfig.Current.SortSyncshellsByVRAM)
            {
                sortedPairs = validPairsInGroup
                    .OrderByDescending(u => string.Equals(u.UserData.UID, groupDto.OwnerUID, StringComparison.Ordinal))
                    .ThenByDescending(u => u.GroupPair[groupDto].GroupPairStatusInfo.IsModerator())
                    .ThenByDescending(u => u.GroupPair[groupDto].GroupPairStatusInfo.IsPinned())
                    .ThenBy(u => u.GetPairSortKey(), StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                sortedPairs = validPairsInGroup
                    .OrderByDescending(u => string.Equals(u.UserData.UID, groupDto.OwnerUID, StringComparison.Ordinal))
                    .ThenByDescending(u => u.GroupPair[groupDto].GroupPairStatusInfo.IsModerator())
                    .ThenByDescending(u => u.GroupPair[groupDto].GroupPairStatusInfo.IsPinned())
                    .ThenByDescending(u => u.LastAppliedApproximateVRAMBytes);
            }

            var visibleUsers = new List<DrawGroupPair>();
            var onlineUsers = new List<DrawGroupPair>();
            var offlineUsers = new List<DrawGroupPair>();
            
            
            foreach (var pair in sortedPairs)
            {
                if (!pair.GroupPair.TryGetValue(groupDto, out var groupPairInfo))
                {
                    continue;
                }

                var drawPair = new DrawGroupPair(
                    groupDto.GID + pair.UserData.UID, pair,
                    ApiController, _mainUi.Mediator, groupDto,
                    groupPairInfo,
                    _uidDisplayHandler,
                    _uiShared,
                    _charaDataManager);

                if (pair.IsVisible)
                    visibleUsers.Add(drawPair);
                else if (pair.IsOnline)
                    onlineUsers.Add(drawPair);
                else
                    offlineUsers.Add(drawPair);
            }

            if (visibleUsers.Count > 0)
            {
                ImGui.TextUnformatted("Visible");
                ImGui.Separator();
                _uidDisplayHandler.RenderPairList(visibleUsers);

                
            }

            if (onlineUsers.Count > 0)
            {
                ImGui.TextUnformatted("Online");
                ImGui.Separator();
                _uidDisplayHandler.RenderPairList(onlineUsers);
            }

            if (offlineUsers.Count > 0)
            {
                ImGui.TextUnformatted("Offline/Unknown");
                ImGui.Separator();
                if (hideOfflineUsers)
                {
                    UiSharedService.ColorText(string.Format(CultureInfo.CurrentCulture, "    {0} offline users omitted from display.", offlineUsers.Count), ImGuiColors.DalamudGrey);
                }
                else
                {
                    _uidDisplayHandler.RenderPairList(offlineUsers);
                }
            }

            ImGui.Separator();
        }
        ImGui.Unindent(20);
    }

    private void DrawSyncShellButtons(GroupFullInfoDto groupDto, List<Pair> groupPairs)
    {
        var infoIcon = FontAwesomeIcon.InfoCircle;

        bool invitesEnabled = !groupDto.GroupPermissions.IsDisableInvites();
        var soundsDisabled = groupDto.GroupPermissions.IsDisableSounds();
        var animDisabled = groupDto.GroupPermissions.IsDisableAnimations();
        var vfxDisabled = groupDto.GroupPermissions.IsDisableVFX();

        var userSoundsDisabled = groupDto.GroupUserPermissions.IsDisableSounds();
        var userAnimDisabled = groupDto.GroupUserPermissions.IsDisableAnimations();
        var userVFXDisabled = groupDto.GroupUserPermissions.IsDisableVFX();

        bool showInfoIcon = !invitesEnabled || soundsDisabled || animDisabled || vfxDisabled || userSoundsDisabled || userAnimDisabled || userVFXDisabled;

        var lockedIcon = invitesEnabled ? FontAwesomeIcon.LockOpen : FontAwesomeIcon.Lock;
        var animIcon = animDisabled ? FontAwesomeIcon.Stop : FontAwesomeIcon.Running;
        var soundsIcon = soundsDisabled ? FontAwesomeIcon.VolumeOff : FontAwesomeIcon.VolumeUp;
        var vfxIcon = vfxDisabled ? FontAwesomeIcon.Circle : FontAwesomeIcon.Sun;
        var userAnimIcon = userAnimDisabled ? FontAwesomeIcon.Stop : FontAwesomeIcon.Running;
        var userSoundsIcon = userSoundsDisabled ? FontAwesomeIcon.VolumeOff : FontAwesomeIcon.VolumeUp;
        var userVFXIcon = userVFXDisabled ? FontAwesomeIcon.Circle : FontAwesomeIcon.Sun;

        var iconSize = UiSharedService.GetIconSize(infoIcon);
        var barbuttonSize = _uiShared.GetIconButtonSize(FontAwesomeIcon.Bars);
        var isOwner = string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal);

        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var windowEndX = ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth();
        var pauseIcon = groupDto.GroupUserPermissions.IsPaused() ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var pauseIconSize = _uiShared.GetIconButtonSize(pauseIcon);

        ImGui.SameLine(windowEndX - barbuttonSize.X - (showInfoIcon ? iconSize.X : 0) - (showInfoIcon ? spacingX : 0) - pauseIconSize.X - spacingX);

        if (showInfoIcon)
        {
            _uiShared.IconText(infoIcon);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                if (!invitesEnabled || soundsDisabled || animDisabled || vfxDisabled)
                {
                    ImGui.TextUnformatted("Syncshell permissions");
                    
                    if (!invitesEnabled)
                    {
                        var lockedText = "Syncshell is closed for joining";
                        _uiShared.IconText(lockedIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(lockedText);
                    }

                    if (soundsDisabled)
                    {
                        var soundsText = "Sound sync disabled through owner";
                        _uiShared.IconText(soundsIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(soundsText);
                    }

                    if (animDisabled)
                    {
                        var animText = "Animation sync disabled through owner";
                        _uiShared.IconText(animIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(animText);
                    }

                    if (vfxDisabled)
                    {
                        var vfxText = "VFX sync disabled through owner";
                        _uiShared.IconText(vfxIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(vfxText);
                    }
                }

                if (userSoundsDisabled || userAnimDisabled || userVFXDisabled)
                {
                    if (!invitesEnabled || soundsDisabled || animDisabled || vfxDisabled)
                        ImGui.Separator();

                    ImGui.TextUnformatted("Your permissions");
                    
                    if (userSoundsDisabled)
                    {
                        var userSoundsText = "Sound sync disabled through you";
                        _uiShared.IconText(userSoundsIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userSoundsText);
                    }

                    if (userAnimDisabled)
                    {
                        var userAnimText = "Animation sync disabled through you";
                        _uiShared.IconText(userAnimIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userAnimText);
                    }

                    if (userVFXDisabled)
                    {
                        var userVFXText = "VFX sync disabled through you";
                        _uiShared.IconText(userVFXIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userVFXText);
                    }

                    if (!invitesEnabled || soundsDisabled || animDisabled || vfxDisabled)
                        UiSharedService.TextWrapped("Note that syncshell permissions for disabling take precedence over your own set permissions");
                }
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
        }

        if (_uiShared.IconButton(pauseIcon))
        {
            var userPerm = groupDto.GroupUserPermissions ^ GroupUserPermissions.Paused;
            _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupDto.Group, new UserData(ApiController.UID), userPerm));
        }
        UiSharedService.AttachToolTip((groupDto.GroupUserPermissions.IsPaused() ? "Resume" : "Pause") + " pairing with all users in this Syncshell");
        ImGui.SameLine();

        if (_uiShared.IconButton(FontAwesomeIcon.Bars))
        {
            ImGui.OpenPopup("ShellPopup");
        }

        if (ImGui.BeginPopup("ShellPopup"))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowCircleLeft, "Leave Syncshell") && UiSharedService.CtrlPressed())
            {
                _ = ApiController.GroupLeave(groupDto);
            }
            UiSharedService.AttachToolTip("Hold CTRL and click to leave this Syncshell" + (!string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal) ? string.Empty : Environment.NewLine
                + "WARNING: This action is irreversible" + Environment.NewLine + "Leaving an owned Syncshell will transfer the ownership to a random person in the Syncshell."));

            if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, "Copy ID"))
            {
                ImGui.CloseCurrentPopup();
                ImGui.SetClipboardText(groupDto.GroupAliasOrGID);
            }
            UiSharedService.AttachToolTip("Copy Syncshell ID to Clipboard");
            
            if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Copy Notes"))
            {
                ImGui.CloseCurrentPopup();
                ImGui.SetClipboardText(UiSharedService.GetNotes(groupPairs));
            }
            UiSharedService.AttachToolTip("Copies all your notes for all users in this Syncshell to the clipboard." + Environment.NewLine + "They can be imported via Settings -> General -> Notes -> Import notes from clipboard");
            
            var soundsText = userSoundsDisabled ? "Enable sound sync" : "Disable sound sync";
            if (_uiShared.IconTextButton(userSoundsIcon, soundsText))
            {
                ImGui.CloseCurrentPopup();
                var perm = groupDto.GroupUserPermissions;
                perm.SetDisableSounds(!perm.IsDisableSounds());
                _ = ApiController.GroupChangeIndividualPermissionState(new(groupDto.Group, new UserData(ApiController.UID), perm));
            }
            UiSharedService.AttachToolTip("Sets your allowance for sound synchronization for users of this syncshell."
                                          + Environment.NewLine + "Disabling the synchronization will stop applying sound modifications for users of this syncshell."
                                          + Environment.NewLine + "Note: this setting can be forcefully overridden to 'disabled' through the syncshell owner."
                                          + Environment.NewLine + "Note: this setting does not apply to individual pairs that are also in the syncshell.");

            var animText = userAnimDisabled ? "Enable animations sync" : "Disable animations sync";
            if (_uiShared.IconTextButton(userAnimIcon, animText))
            {
                ImGui.CloseCurrentPopup();
                var perm = groupDto.GroupUserPermissions;
                perm.SetDisableAnimations(!perm.IsDisableAnimations());
                _ = ApiController.GroupChangeIndividualPermissionState(new(groupDto.Group, new UserData(ApiController.UID), perm));
            }
            UiSharedService.AttachToolTip("Sets your allowance for animations synchronization for users of this syncshell."
                                          + Environment.NewLine + "Disabling the synchronization will stop applying animations modifications for users of this syncshell."
                                          + Environment.NewLine + "Note: this setting might also affect sound synchronization"
                                          + Environment.NewLine + "Note: this setting can be forcefully overridden to 'disabled' through the syncshell owner."
                                          + Environment.NewLine + "Note: this setting does not apply to individual pairs that are also in the syncshell.");

            var vfxText = userVFXDisabled ? "Enable VFX sync" : "Disable VFX sync";
            if (_uiShared.IconTextButton(userVFXIcon, vfxText))
            {
                ImGui.CloseCurrentPopup();
                var perm = groupDto.GroupUserPermissions;
                perm.SetDisableVFX(!perm.IsDisableVFX());
                _ = ApiController.GroupChangeIndividualPermissionState(new(groupDto.Group, new UserData(ApiController.UID), perm));
            }
            UiSharedService.AttachToolTip("Sets your allowance for VFX synchronization for users of this syncshell."
                                          + Environment.NewLine + "Disabling the synchronization will stop applying VFX modifications for users of this syncshell."
                                          + Environment.NewLine + "Note: this setting might also affect animation synchronization to some degree"
                                          + Environment.NewLine + "Note: this setting can be forcefully overridden to 'disabled' through the syncshell owner."
                                          + Environment.NewLine + "Note: this setting does not apply to individual pairs that are also in the syncshell.");

            if (isOwner || groupDto.GroupUserInfo.IsModerator())
            {
                ImGui.Separator();
                if (_uiShared.IconTextButton(FontAwesomeIcon.Cog, "Open Admin Panel"))
                {
                    ImGui.CloseCurrentPopup();
                    _mainUi.Mediator.Publish(new OpenSyncshellAdminPanel(groupDto));
                }
            }

            ImGui.EndPopup();
        }
    }

    private void DrawSyncshellList(float ySize)
    {
        ImGui.BeginChild("list", new Vector2(_mainUi.WindowContentWidth, ySize), border: false);
        foreach (var entry in _pairManager.GroupPairs.OrderBy(g => g.Key.Group.AliasOrGID, StringComparer.OrdinalIgnoreCase).ToList())
        {
            using (ImRaii.PushId(entry.Key.Group.GID)) DrawSyncshell(entry.Key, entry.Value);
        }
        ImGui.EndChild();
    }
}