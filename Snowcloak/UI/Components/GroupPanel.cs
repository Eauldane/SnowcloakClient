using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using ElezenTools.UI;
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
using Snowcloak.UI;
using Snowcloak.UI.Handlers;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI.Components;

internal sealed class GroupPanel
{
    private readonly Dictionary<string, bool> _expandedGroupState = new(StringComparer.Ordinal);
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SnowMediator _mediator;
    private readonly PairManager _pairManager;
    private readonly SnowcloakConfigService _snowcloakConfig;
    private readonly SyncshellBudgetPanel _syncshellBudgetPanel;
    private readonly NotesStore _notesStore;
    private readonly ShellConfigStore _shellConfigStore;
    private readonly CharaDataManager _charaDataManager;
    private readonly Dictionary<string, bool> _showGidForEntry = new(StringComparer.Ordinal);
    private readonly UidDisplayHandler _uidDisplayHandler;
    private string _editGroupComment = string.Empty;
    private string _editGroupEntry = string.Empty;
    private bool _errorGroupCreate;
    private bool _errorGroupJoin;
    private GroupPasswordDto? _lastCreatedGroup;
    private bool _showModalCreateGroup;
    private bool _showModalEnterPassword;
    private bool _showRegionJoinError;
    private string? _pendingRegionalShellAlias;
    private string _syncShellPassword = string.Empty;
    private string _syncShellToJoin = string.Empty;
    private bool _showPublicSyncshellWarning;
    private string? _publicSyncshellAliasToJoin;
    private readonly ConcurrentDictionary<string, GroupCommunityDto> _communityCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _communityLoading = new(StringComparer.Ordinal);

    public GroupPanel(SnowMediator mediator, ApiController apiController, DalamudUtilService dalamudUtilService, PairManager pairManager,
        UidDisplayHandler uidDisplayHandler, SnowcloakConfigService snowcloakConfig, NotesStore notesStore, ShellConfigStore shellConfigStore,
        CharaDataManager charaDataManager, SyncshellBudgetService syncshellBudgetService)
    {
        _mediator = mediator;
        _apiController = apiController;
        _dalamudUtilService = dalamudUtilService;
        _pairManager = pairManager;
        _uidDisplayHandler = uidDisplayHandler;
        _snowcloakConfig = snowcloakConfig;
        _syncshellBudgetPanel = new(syncshellBudgetService);
        _notesStore = notesStore;
        _shellConfigStore = shellConfigStore;
        _charaDataManager = charaDataManager;
    }

    private ApiController ApiController => _apiController;

    public float DrawSyncshells(float contentWidth)
    {
        HandlePendingRegionalShell();
        using (ImRaii.PushId("addsyncshell")) DrawAddSyncshell();
        using (ImRaii.PushId("communitybrowse")) DrawCommunityBrowseButton();

        var listHeight = Math.Max(1f, ImGui.GetContentRegionAvail().Y - GetRegionJoinButtonHeight());

        using (ImRaii.PushId("syncshelllist")) DrawSyncshellList(contentWidth, listHeight);
        using (ImRaii.PushId("regionaljoin")) DrawRegionJoinButton();

        return ImGui.GetCursorPosY();
    }

    private void DrawAddSyncshell()
    {
        var framePadding = ImGui.GetStyle().FramePadding;
        var tallPadding = new Vector2(framePadding.X, framePadding.Y + 4f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, tallPadding);
        var buttonSize = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Plus);
        var clearButtonSize = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Times);
        var entryIcon = FontAwesomeIcon.Users;
        var entryIconWidth = ElezenImgui.GetIconData(entryIcon).X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
        {
            ElezenImgui.ShowIcon(entryIcon);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ElezenImgui.GetWindowContentRegionWidth()
            - ImGui.GetWindowContentRegionMin().X
            - entryIconWidth
            - clearButtonSize.X
            - buttonSize.X
            - spacing * 3);
        ImGui.InputTextWithHint("##syncshellid", "Syncshell GID/Alias (leave empty to create)", ref _syncShellToJoin, 20);
        ImGui.SameLine();
        if (ElezenImgui.IconButton(FontAwesomeIcon.Times))
        {
            _syncShellToJoin = string.Empty;
        }
        ElezenImgui.AttachTooltip("Clear");
        ImGui.SameLine();

        bool userCanJoinMoreGroups = _pairManager.GroupPairs.Count < ApiController.ServerInfo.MaxGroupsJoinedByUser;
        bool userCanCreateMoreGroups = _pairManager.GroupPairs.Count(u => string.Equals(u.Key.Owner.UID, ApiController.UID, StringComparison.Ordinal)) < ApiController.ServerInfo.MaxGroupsCreatedByUser;
        bool alreadyInGroup = _pairManager.GroupPairs.Select(p => p.Key).Any(p => string.Equals(p.Group.Alias, _syncShellToJoin, StringComparison.Ordinal)
            || string.Equals(p.Group.GID, _syncShellToJoin, StringComparison.Ordinal));

        if (alreadyInGroup) ImGui.BeginDisabled();
        if (ElezenImgui.IconButton(FontAwesomeIcon.Plus))
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
        ElezenImgui.AttachTooltip(_syncShellToJoin.IsNullOrEmpty()
            ? (userCanCreateMoreGroups ? "Create a new Syncshell" : string.Format(CultureInfo.CurrentCulture, "You cannot create more than {0} Syncshells", ApiController.ServerInfo.MaxGroupsCreatedByUser))
            : (userCanJoinMoreGroups ? string.Format(CultureInfo.CurrentCulture, "Join Syncshell {0}", _syncShellToJoin) : string.Format(CultureInfo.CurrentCulture, "You cannot join more than {0} Syncshells", ApiController.ServerInfo.MaxGroupsJoinedByUser)));

        if (alreadyInGroup) ImGui.EndDisabled();
        ImGui.PopStyleVar();

        var enterPasswordTitle = "Enter Syncshell Password";
        if (ImGui.BeginPopupModal(enterPasswordTitle, ref _showModalEnterPassword, SnowcloakUi.PopupWindowFlags))
        {
            ElezenImgui.WrappedText("Joining a syncshell means you will be paired with all of its members.");
            ElezenImgui.WrappedText("You will be able to see all of their mods; and they will be able to see all of yours.");
            ElezenImgui.WrappedText("Please only proceed if you are okay with that.");
            ImGui.Separator();
            ElezenImgui.WrappedText(string.Format(CultureInfo.CurrentCulture, "Enter the password for Syncshell {0}:", _syncShellToJoin));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##password", string.Format(CultureInfo.CurrentCulture, "{0} Password", _syncShellToJoin), ref _syncShellPassword, 255, ImGuiInputTextFlags.Password);
            if (_errorGroupJoin)
            {
                ElezenImgui.ColouredWrappedText(string.Format(CultureInfo.CurrentCulture, "An error occured during joining of this Syncshell: you either have joined the maximum amount of Syncshells ({0}), it does not exist, the password you entered is wrong, you already joined the Syncshell, the Syncshell is full, or the Syncshell has closed invites.",
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
            ElezenImgui.SetScaledWindowSize(290);
            ImGui.EndPopup();
        }

        var createSyncshellTitle = "Create Syncshell";
        if (ImGui.BeginPopupModal(createSyncshellTitle, ref _showModalCreateGroup, SnowcloakUi.PopupWindowFlags))
        {
            ElezenImgui.WrappedText("Creating a syncshell means you are responsible for ensuring proper moderation.");
            ElezenImgui.WrappedText("Please only proceed if you are prepared to enforce community guidelines and keep your members safe.");
            ElezenImgui.WrappedText("Unmoderated synchells are not permitted. Administrator action against unmoderated shells may be performed at staff discretion.");
            ElezenImgui.WrappedText("Press the button below to create a new Syncshell.");
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
                if (ElezenImgui.IconButton(FontAwesomeIcon.Copy))
                {
                    ImGui.SetClipboardText(_lastCreatedGroup.Password);
                }
                ElezenImgui.WrappedText("You can change the Syncshell password later at any time.");
            }

            if (_errorGroupCreate)
            {
                ElezenImgui.ColouredWrappedText("You are already owner of the maximum amount of Syncshells or joined the maximum amount of Syncshells. Relinquish ownership of your own Syncshells to someone else or leave existing Syncshells.",
                    new Vector4(1, 0, 0, 1));
            }

            ElezenImgui.SetScaledWindowSize(350);
            ImGui.EndPopup();
        }

        ImGuiHelpers.ScaledDummy(2);
    }

    private void DrawCommunityBrowseButton()
    {
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Globe, "Browse Community Syncshells"))
        {
            _mediator.Publish(new UiToggleMessage(typeof(CommunitySyncshellWindow)));
        }
        ElezenImgui.AttachTooltip("Open the community syncshell directory to search and join public syncshells.");
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
        var regionName = _dalamudUtilService.GetDataCenterRegion();
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
                ElezenImgui.AttachTooltip(alreadyInRegionShell
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
            ElezenImgui.AttachTooltip("Regional Syncshell is unavailable because your datacenter region could not be determined.");
        }

        if (_showRegionJoinError)
            ElezenImgui.ColouredWrappedText("The regional syncshell you're trying to join is either full and awaiting server expansion, or currently unavailable. Try again later!", ImGuiColors.DalamudRed);
        
        
        DrawPublicSyncshellWarningModal();
    }

    private void DrawPublicSyncshellWarningModal()
    {
        var popupTitle = "Join Public Syncshell";
        if (ImGui.BeginPopupModal(popupTitle, ref _showPublicSyncshellWarning, SnowcloakUi.PopupWindowFlags))
        {
            ElezenImgui.WrappedText("Public Syncshells can contain lots of people, and these people may gather in one particular spot.");
            ElezenImgui.WrappedText("This can cause significant load on both the server, and your PC. If you have a lower-end computer, we STRONGLY recommend that you do not join public syncshells.");
            ElezenImgui.WrappedText("Please note that these syncshells are not provided with the intention of facilitating large gatherings. In the event that one occurs, public syncshells will be automatically disabled and paused for all members to protect the functionality of the rest of the service.");

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

            ElezenImgui.SetScaledWindowSize(500);
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

        string resolvedGid;
        bool resolved;
        if (matchingGroup.Key is not null)
        {
            resolved = true;
            resolvedGid = matchingGroup.Key.GID;
        }
        else
        {
            resolved = false;
            resolvedGid = regionShell;
        }

        DisableRegionalShellChat(resolvedGid);

        if (!string.Equals(resolvedGid, regionShell, StringComparison.Ordinal))
        {
            DisableRegionalShellChat(regionShell);
        }

        return resolved;
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
        var shellConfig = _shellConfigStore.GetShellConfigForGid(gid);
        if (shellConfig.Enabled)
        {
            shellConfig.Enabled = false;
            _shellConfigStore.SaveShellConfigForGid(gid, shellConfig);
        }
    }
    
    private void DrawSyncshell(GroupFullInfoDto groupDto, List<Pair> pairsInGroup)
    {
        var validPairsInGroup = pairsInGroup
            .Where(p => p.GroupPair.ContainsKey(groupDto))
            .ToList();

        if (!_expandedGroupState.TryGetValue(groupDto.GID, out bool isExpanded))
        {
            isExpanded = false;
            _expandedGroupState.Add(groupDto.GID, isExpanded);
        }

        ImGui.AlignTextToFramePadding();
        var icon = isExpanded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight;
        ElezenImgui.ShowIcon(icon);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _expandedGroupState[groupDto.GID] = !_expandedGroupState[groupDto.GID];
        }
        ImGui.SameLine();

        var textIsGid = true;
        string groupName = groupDto.GroupAliasOrGID;

        if (string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Crown.ToIconString());
            ImGui.PopFont();
            ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "You are the owner of Syncshell {0}", groupName));
            ImGui.SameLine();
        }
        else if (groupDto.GroupUserInfo.IsModerator())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.UserShield.ToIconString());
            ImGui.PopFont();
            ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "You are a moderator of Syncshell {0}", groupName));
            ImGui.SameLine();
        }

        _showGidForEntry.TryGetValue(groupDto.GID, out var showGidInsteadOfName);
        var groupComment = _notesStore.GetNoteForGid(groupDto.GID);
        if (!showGidInsteadOfName && !string.IsNullOrEmpty(groupComment))
        {
            groupName = groupComment;
            textIsGid = false;
        }

        if (!string.Equals(_editGroupEntry, groupDto.GID, StringComparison.Ordinal))
        {
            var shellConfig = _shellConfigStore.GetShellConfigForGid(groupDto.GID);
            if (!_snowcloakConfig.Current.DisableChat && shellConfig.Enabled)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted($"[{shellConfig.ShellNumber}]");
                ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "Chat command prefix: /ss{0}", shellConfig.ShellNumber));
                ImGui.SameLine();
            }
            if (textIsGid) ImGui.PushFont(UiBuilder.MonoFont);
            ImGui.TextColored(ElezenTools.UI.Colour.HexToVector4(groupDto.Group.DisplayColour), groupName);
            if (textIsGid) ImGui.PopFont();
            ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "Left click to switch between GID display and comment{0}Right click to change comment for {1}{0}{0}Users: {2}, Owner: {3}",
                Environment.NewLine, groupName, validPairsInGroup.Count + 1, groupDto.OwnerAliasOrUID));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                var prevState = _showGidForEntry.TryGetValue(groupDto.GID, out var showGid)
                    ? showGid
                    : textIsGid;

                _showGidForEntry[groupDto.GID] = !prevState;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _notesStore.SetNoteForGid(_editGroupEntry, _editGroupComment);
                _editGroupComment = _notesStore.GetNoteForGid(groupDto.GID) ?? string.Empty;
                _editGroupEntry = groupDto.GID;
            }

            var community = GetCommunity(groupDto);
            DrawEventIndicator(groupDto, community);
            DrawSyncshellWorldTag(community);
        }
        else
        {
            var buttonSizes = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Bars).X + ElezenImgui.GetIconButtonSize(FontAwesomeIcon.LockOpen).X;
            ImGui.SetNextItemWidth(ElezenImgui.GetWindowContentRegionWidth() - ImGui.GetCursorPosX() - buttonSizes - ImGui.GetStyle().ItemSpacing.X * 2);
            if (ImGui.InputTextWithHint("", "Comment/Notes", ref _editGroupComment, 255, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                _notesStore.SetNoteForGid(groupDto.GID, _editGroupComment);
                _editGroupEntry = string.Empty;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _editGroupEntry = string.Empty;
            }
            ElezenImgui.AttachTooltip("Hit ENTER to save\nRight click to cancel");
        }


        using (ImRaii.PushId(groupDto.GID + "settings")) DrawSyncShellButtons(groupDto, validPairsInGroup);

        bool hideOfflineUsers = validPairsInGroup.Count > 1000;
        
        ImGui.Indent(20);
        if (_expandedGroupState[groupDto.GID])
        {
            DrawGroupCommunity(groupDto);
            ImGui.Separator();

            if (_snowcloakConfig.Current.ShowSyncshellBudgetDashboard)
            {
                var budgetPairs = validPairsInGroup
                    .Where(p => !string.Equals(p.UserData.UID, ApiController.UID, StringComparison.Ordinal))
                    .ToList();

                _syncshellBudgetPanel.Draw(groupDto, budgetPairs);
                ImGui.Separator();
            }

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
            var pausedUsers = new List<DrawGroupPair>();
            var offlineUsers = new List<DrawGroupPair>();
            
            
            foreach (var pair in sortedPairs)
            {
                if (!pair.GroupPair.TryGetValue(groupDto, out var groupPairInfo))
                {
                    continue;
                }

                var drawPair = new DrawGroupPair(
                    groupDto.GID + pair.UserData.UID, pair,
                    ApiController, _mediator, groupDto,
                    groupPairInfo,
                    _uidDisplayHandler,
                    _charaDataManager,
                    _snowcloakConfig);

                bool pausedByYou;
                bool pausedByOther;
                if (pair.UserPair != null)
                {
                    pausedByYou = pair.UserPair.OwnPermissions.IsPaused();
                    pausedByOther = pair.UserPair.OtherPermissions.IsPaused();
                }
                else
                {
                    pausedByYou = groupDto.GroupUserPermissions.IsPaused()
                        || groupPairInfo.OwnGroupUserPermissions.IsPaused();
                    pausedByOther = groupPairInfo.OtherGroupUserPermissions.IsPaused();
                }

                bool showAsOffline = pausedByOther && !pausedByYou;
                if (pausedByYou)
                    pausedUsers.Add(drawPair);
                else if (!showAsOffline && pair.IsVisible)
                    visibleUsers.Add(drawPair);
                else if (!showAsOffline && pair.IsOnline)
                    onlineUsers.Add(drawPair);
                else
                    offlineUsers.Add(drawPair);
            }

            if (visibleUsers.Count > 0)
            {
                ImGui.TextUnformatted("Visible");
                ImGui.Separator();
                UidDisplayHandler.RenderPairList(visibleUsers);

                
            }

            if (onlineUsers.Count > 0)
            {
                ImGui.TextUnformatted("Online");
                ImGui.Separator();
                UidDisplayHandler.RenderPairList(onlineUsers);
            }

            if (pausedUsers.Count > 0)
            {
                ImGui.TextUnformatted("Paused");
                ImGui.Separator();
                UidDisplayHandler.RenderPairList(pausedUsers);
            }

            if (offlineUsers.Count > 0)
            {
                ImGui.TextUnformatted("Offline/Unknown");
                ImGui.Separator();
                if (hideOfflineUsers)
                {
                    ElezenImgui.ColouredText(string.Format(CultureInfo.CurrentCulture, "    {0} offline users omitted from display.", offlineUsers.Count), ImGuiColors.DalamudGrey);
                }
                else
                {
                    UidDisplayHandler.RenderPairList(offlineUsers);
                }
            }

            ImGui.Separator();
        }
        ImGui.Unindent(20);
    }

    private void DrawGroupCommunity(GroupFullInfoDto groupDto)
    {
        var community = GetCommunity(groupDto);

        // Events live in their own window now (opened from the calendar indicator or the
        // syncshell menu); the expanded row keeps only the message of the day.
        if (!string.IsNullOrWhiteSpace(community.Motd))
        {
            ElezenImgui.WrappedText(community.Motd);
        }
    }

    private GroupCommunityDto GetCommunity(GroupFullInfoDto groupDto)
    {
        if (_communityCache.TryGetValue(groupDto.GID, out var community))
        {
            return community;
        }

        // Load community details off the draw thread; render an empty record until it
        // arrives rather than blocking the frame on a SignalR round-trip (the old
        // `.Result` call stalled or deadlocked the UI thread).
        QueueCommunityLoad(groupDto);
        return new GroupCommunityDto(groupDto.Group);
    }

    private void QueueCommunityLoad(GroupFullInfoDto groupDto)
    {
        // Single-flight per syncshell: at most one in-flight request per GID.
        if (!_communityLoading.TryAdd(groupDto.GID, true))
        {
            return;
        }

        _ = LoadCommunityAsync(groupDto);
    }

    private async Task LoadCommunityAsync(GroupFullInfoDto groupDto)
    {
        GroupCommunityDto? community;
        try
        {
            community = await _apiController.GroupGetCommunity(new GroupDto(groupDto.Group)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Community MOTD/events are optional chrome. If the lookup fails (not connected,
            // or the shell has no community record) cache an empty record so the row renders
            // without it and we do not re-request it every frame.
            community = null;
        }

        _communityCache[groupDto.GID] = community ?? new GroupCommunityDto(groupDto.Group);
        _communityLoading.TryRemove(groupDto.GID, out _);
    }

    // An event counts as "active" for one hour after its start time.
    private static readonly TimeSpan EventActiveWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// Draws a clickable calendar indicator beside the syncshell name when it has events
    /// that are running or still to come. Clicking opens the dedicated events window.
    /// </summary>
    private void DrawEventIndicator(GroupFullInfoDto groupDto, GroupCommunityDto community)
    {
        var summary = GetEventSummary(community, DateTime.UtcNow);
        if (!summary.HasAny)
        {
            return;
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        var colour = summary.AnyActive ? ImGuiColors.HealerGreen : SnowcloakColours.OnlineBlue;
        ElezenImgui.ShowIcon(FontAwesomeIcon.CalendarDay, colour);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _mediator.Publish(new OpenSyncshellEventsWindow(groupDto));
        }
        ElezenImgui.AttachTooltip(BuildIndicatorTooltip(summary));
    }

    /// <summary>Shows the admin-configured location (world, else region) next to the syncshell name.</summary>
    private void DrawSyncshellWorldTag(GroupCommunityDto community)
    {
        var locationText = _dalamudUtilService.GetWorldName(community.MainWorldId) ?? community.MainRegion;
        if (string.IsNullOrEmpty(locationText))
        {
            return;
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
        {
            ImGui.TextUnformatted("· " + locationText);
        }
        ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "This syncshell is based in {0}.", locationText));
    }

    private static (bool HasAny, bool AnyActive, int Count, GroupEventDto? Next) GetEventSummary(GroupCommunityDto community, DateTime nowUtc)
    {
        var anyActive = false;
        var count = 0;
        GroupEventDto? next = null;
        foreach (var shellEvent in community.Events)
        {
            var start = DateTime.SpecifyKind(shellEvent.StartsAtUtc, DateTimeKind.Utc);
            var active = start <= nowUtc && nowUtc < start + EventActiveWindow;
            var upcoming = start > nowUtc;
            if (!active && !upcoming)
            {
                continue;
            }

            count++;
            anyActive |= active;
            if (upcoming && (next == null || start < DateTime.SpecifyKind(next.StartsAtUtc, DateTimeKind.Utc)))
            {
                next = shellEvent;
            }
        }

        return (count > 0, anyActive, count, next);
    }

    private static string BuildIndicatorTooltip((bool HasAny, bool AnyActive, int Count, GroupEventDto? Next) summary)
    {
        var header = summary.AnyActive
            ? "An event is in progress now."
            : string.Format(CultureInfo.CurrentCulture, "{0} upcoming event{1}.", summary.Count, summary.Count == 1 ? string.Empty : "s");

        if (summary.Next != null)
        {
            var startLocal = DateTime.SpecifyKind(summary.Next.StartsAtUtc, DateTimeKind.Utc).ToLocalTime();
            header += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, "Next: {0:g}  {1}", startLocal, summary.Next.Title);
        }

        return header + Environment.NewLine + "Click to view events.";
    }

    private void DrawSyncShellButtons(GroupFullInfoDto groupDto, List<Pair> groupPairs)
    {
        var infoIcon = FontAwesomeIcon.InfoCircle;

        var shellConfig = _shellConfigStore.GetShellConfigForGid(groupDto.GID);
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

        var actionButtonSize = DrawPairBase.RowActionButtonSize;
        var isOwner = string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal);

        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var windowEndX = ImGui.GetWindowContentRegionMin().X + ElezenImgui.GetWindowContentRegionWidth();
        var pauseIcon = groupDto.GroupUserPermissions.IsPaused() ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;

        ImGui.SameLine(windowEndX
            - actionButtonSize.X
            - (showInfoIcon ? actionButtonSize.X + spacingX : 0)
            - actionButtonSize.X
            - spacingX);

        if (showInfoIcon)
        {
            DrawPairBase.DrawRowActionButton(infoIcon, "syncshell-info");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                if (!invitesEnabled || soundsDisabled || animDisabled || vfxDisabled)
                {
                    ImGui.TextUnformatted("Syncshell permissions");
                    
                    if (!invitesEnabled)
                    {
                        var lockedText = "Syncshell is closed for joining";
                        ElezenImgui.ShowIcon(lockedIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(lockedText);
                    }

                    if (soundsDisabled)
                    {
                        var soundsText = "Sound sync disabled through owner";
                        ElezenImgui.ShowIcon(soundsIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(soundsText);
                    }

                    if (animDisabled)
                    {
                        var animText = "Animation sync disabled through owner";
                        ElezenImgui.ShowIcon(animIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(animText);
                    }

                    if (vfxDisabled)
                    {
                        var vfxText = "VFX sync disabled through owner";
                        ElezenImgui.ShowIcon(vfxIcon);
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
                        ElezenImgui.ShowIcon(userSoundsIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userSoundsText);
                    }

                    if (userAnimDisabled)
                    {
                        var userAnimText = "Animation sync disabled through you";
                        ElezenImgui.ShowIcon(userAnimIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userAnimText);
                    }

                    if (userVFXDisabled)
                    {
                        var userVFXText = "VFX sync disabled through you";
                        ElezenImgui.ShowIcon(userVFXIcon);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userVFXText);
                    }

                    if (!invitesEnabled || soundsDisabled || animDisabled || vfxDisabled)
                        ElezenImgui.WrappedText("Note that syncshell permissions for disabling take precedence over your own set permissions");
                }
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
        }

        if (DrawPairBase.DrawRowActionButton(pauseIcon, "syncshell-pause"))
        {
            var userPerm = groupDto.GroupUserPermissions ^ GroupUserPermissions.Paused;
            _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(groupDto.Group, new UserData(ApiController.UID), userPerm));
        }
        ElezenImgui.AttachTooltip((groupDto.GroupUserPermissions.IsPaused() ? "Resume" : "Pause") + " pairing with all users in this Syncshell");
        ImGui.SameLine();

        if (DrawPairBase.DrawRowActionButton(FontAwesomeIcon.Bars, "syncshell-menu", SnowcloakColours.CompactTextMuted))
        {
            ImGui.OpenPopup("ShellPopup");
        }

        if (ImGui.BeginPopup("ShellPopup"))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowCircleLeft, "Leave Syncshell") && ElezenImgui.CtrlPressed())
            {
                _ = ApiController.GroupLeave(groupDto);
            }
            ElezenImgui.AttachTooltip("Hold CTRL and click to leave this Syncshell" + (!string.Equals(groupDto.OwnerUID, ApiController.UID, StringComparison.Ordinal) ? string.Empty : Environment.NewLine
                + "WARNING: This action is irreversible" + Environment.NewLine + "Leaving an owned Syncshell will transfer the ownership to a random person in the Syncshell."));

            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Copy, "Copy ID"))
            {
                ImGui.CloseCurrentPopup();
                ImGui.SetClipboardText(groupDto.GroupAliasOrGID);
            }
            ElezenImgui.AttachTooltip("Copy Syncshell ID to Clipboard");
            
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.StickyNote, "Copy Notes"))
            {
                ImGui.CloseCurrentPopup();
                ImGui.SetClipboardText(NotesStore.ExportNotes(groupPairs));
            }
            ElezenImgui.AttachTooltip("Copies all your notes for all users in this Syncshell to the clipboard." + Environment.NewLine + "They can be imported via Settings -> General -> Notes -> Import notes from clipboard");

            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.CalendarDay, "View Events"))
            {
                ImGui.CloseCurrentPopup();
                _mediator.Publish(new OpenSyncshellEventsWindow(groupDto));
            }
            ElezenImgui.AttachTooltip("Open upcoming and active events for this Syncshell.");

            var chatText = shellConfig.Enabled ? "Leave chat" : "Join chat";
            using (ImRaii.Disabled(!ApiController.IsConnected))
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Comments, chatText))
                {
                    ImGui.CloseCurrentPopup();
                    shellConfig.Enabled = !shellConfig.Enabled;
                    _shellConfigStore.SaveShellConfigForGid(groupDto.GID, shellConfig);

                    if (shellConfig.Enabled)
                    {
                        _ = ApiController.GroupChatJoin(new GroupDto(groupDto.Group));
                    }
                    else
                    {
                        _ = ApiController.GroupChatLeave(new GroupDto(groupDto.Group));
                    }
                }
            }
            ElezenImgui.AttachTooltip("Toggle whether this syncshell appears in your chat window and receives chat messages.");
            
            var soundsText = userSoundsDisabled ? "Enable sound sync" : "Disable sound sync";
            if (ElezenImgui.ShowIconButton(userSoundsIcon, soundsText))
            {
                ImGui.CloseCurrentPopup();
                var perm = groupDto.GroupUserPermissions;
                perm.SetDisableSounds(!perm.IsDisableSounds());
                _ = ApiController.GroupChangeIndividualPermissionState(new(groupDto.Group, new UserData(ApiController.UID), perm));
            }
            ElezenImgui.AttachTooltip("Sets your allowance for sound synchronization for users of this syncshell."
                                          + Environment.NewLine + "Disabling the synchronization will stop applying sound modifications for users of this syncshell."
                                          + Environment.NewLine + "Note: this setting can be forcefully overridden to 'disabled' through the syncshell owner."
                                          + Environment.NewLine + "Note: this setting does not apply to individual pairs that are also in the syncshell.");

            var animText = userAnimDisabled ? "Enable animations sync" : "Disable animations sync";
            if (ElezenImgui.ShowIconButton(userAnimIcon, animText))
            {
                ImGui.CloseCurrentPopup();
                var perm = groupDto.GroupUserPermissions;
                perm.SetDisableAnimations(!perm.IsDisableAnimations());
                _ = ApiController.GroupChangeIndividualPermissionState(new(groupDto.Group, new UserData(ApiController.UID), perm));
            }
            ElezenImgui.AttachTooltip("Sets your allowance for animations synchronization for users of this syncshell."
                                          + Environment.NewLine + "Disabling the synchronization will stop applying animations modifications for users of this syncshell."
                                          + Environment.NewLine + "Note: this setting might also affect sound synchronization"
                                          + Environment.NewLine + "Note: this setting can be forcefully overridden to 'disabled' through the syncshell owner."
                                          + Environment.NewLine + "Note: this setting does not apply to individual pairs that are also in the syncshell.");

            var vfxText = userVFXDisabled ? "Enable VFX sync" : "Disable VFX sync";
            if (ElezenImgui.ShowIconButton(userVFXIcon, vfxText))
            {
                ImGui.CloseCurrentPopup();
                var perm = groupDto.GroupUserPermissions;
                perm.SetDisableVFX(!perm.IsDisableVFX());
                _ = ApiController.GroupChangeIndividualPermissionState(new(groupDto.Group, new UserData(ApiController.UID), perm));
            }
            ElezenImgui.AttachTooltip("Sets your allowance for VFX synchronization for users of this syncshell."
                                          + Environment.NewLine + "Disabling the synchronization will stop applying VFX modifications for users of this syncshell."
                                          + Environment.NewLine + "Note: this setting might also affect animation synchronization to some degree"
                                          + Environment.NewLine + "Note: this setting can be forcefully overridden to 'disabled' through the syncshell owner."
                                          + Environment.NewLine + "Note: this setting does not apply to individual pairs that are also in the syncshell.");

            if (isOwner || groupDto.GroupUserInfo.IsModerator())
            {
                ImGui.Separator();
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Cog, "Open Admin Panel"))
                {
                    ImGui.CloseCurrentPopup();
                    _mediator.Publish(new OpenSyncshellAdminPanel(groupDto));
                }
            }

            ImGui.EndPopup();
        }
    }

    private void DrawSyncshellList(float contentWidth, float ySize)
    {
        ImGui.BeginChild("list", new Vector2(contentWidth, ySize), border: false);
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, 7f * ImGuiHelpers.GlobalScale)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2f * ImGuiHelpers.GlobalScale)))
        {
            foreach (var entry in _pairManager.GroupPairs.OrderBy(g => g.Key.Group.AliasOrGID, StringComparer.OrdinalIgnoreCase).ToList())
            {
                using (ImRaii.PushId(entry.Key.Group.GID)) DrawSyncshell(entry.Key, entry.Value);
            }
        }
        ImGui.EndChild();
    }

}
