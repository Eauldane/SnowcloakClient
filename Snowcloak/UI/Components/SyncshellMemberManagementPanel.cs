using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;

namespace Snowcloak.UI.Components;

internal sealed class SyncshellMemberManagementPanel
{
    private readonly ApiController _apiController;
    private readonly SnowMediator _mediator;
    private readonly PairManager _pairManager;
    private readonly Dictionary<string, AsyncOp> _memberOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AsyncOp<bool>> _unbanOperations = new(StringComparer.Ordinal);
    private readonly AsyncOp<List<BannedGroupUserDto>> _banRefreshOperation = new();
    private readonly AsyncOp _clearOperation = new();
    private readonly AsyncOp<int> _prunePreviewOperation = new();
    private readonly AsyncOp<int> _pruneOperation = new();
    private List<BannedGroupUserDto> _bannedUsers = [];
    private string _banStatus = string.Empty;
    private int _pruneDays = 14;

    public SyncshellMemberManagementPanel(ApiController apiController, SnowMediator mediator, PairManager pairManager)
    {
        _apiController = apiController;
        _mediator = mediator;
        _pairManager = pairManager;
    }

    public void DrawMembers(GroupFullInfoDto group, bool isOwner, bool isModerator, Action<GroupPairFullInfoDto> openMemberLabelEditor)
    {
        if (!_pairManager.GroupPairs.TryGetValue(group, out var pairs))
        {
            ElezenImgui.ColouredWrappedText("No users found in this Syncshell", ImGuiColors.DalamudYellow);
            return;
        }

        using var table = ImRaii.Table("userList#" + group.Group.GID, 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Alias/UID/Note", ImGuiTableColumnFlags.None, 3);
        ImGui.TableSetupColumn("Online/Name", ImGuiTableColumnFlags.None, 2);
        ImGui.TableSetupColumn("Roles", ImGuiTableColumnFlags.None, 2);
        ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.None, 1);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.None, 3);
        ImGui.TableHeadersRow();

        foreach (var row in BuildRows(group, pairs))
        {
            DrawMemberRow(group, row, isOwner, isModerator, openMemberLabelEditor);
        }
    }

    public void DrawCleanup(GroupFullInfoDto group)
    {
        using (ImRaii.Disabled(!ElezenImgui.CtrlPressed() || _clearOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Broom, "Clear Syncshell"))
            {
                _ = _clearOperation.Run(() => _apiController.GroupClear(new(group.Group)));
            }
        }
        ElezenImgui.AttachTooltip("This will remove all non-pinned, non-moderator users from the Syncshell."
                                      + ElezenImgui.TooltipSeparator + "Hold CTRL to enable this button");
        DrawOperationStatus(_clearOperation, "Clearing Syncshell...");

        ImGuiHelpers.ScaledDummy(2f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Unlink, "Check for Inactive Users"))
        {
            _pruneOperation.Reset();
            _ = _prunePreviewOperation.Run(() => _apiController.GroupPrune(new(group.Group), _pruneDays, execute: false));
        }
        ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "This will start the prune process for this Syncshell of inactive users that have not logged in the past {0} days.", _pruneDays)
                                      + Environment.NewLine + "You will be able to review the amount of inactive users before executing the prune."
                                      + ElezenImgui.TooltipSeparator + "Note: pruning excludes pinned users and moderators of this Syncshell.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("Days of inactivity", string.Format(CultureInfo.CurrentCulture, "{0} days", _pruneDays)))
        {
            foreach (var days in new[] { 7, 14, 30, 90 })
            {
                if (ImGui.Selectable(string.Format(CultureInfo.CurrentCulture, "{0} days", days), _pruneDays == days))
                {
                    _pruneDays = days;
                    _prunePreviewOperation.Reset();
                    _pruneOperation.Reset();
                }
            }

            ImGui.EndCombo();
        }

        DrawPruneStatus(group);
    }

    public void DrawBans(GroupFullInfoDto group)
    {
        ConsumeBanRefresh();
        ConsumeUnbans();

        using (ImRaii.Disabled(_banRefreshOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Retweet, "Refresh Banlist from Server"))
            {
                _banStatus = string.Empty;
                _ = _banRefreshOperation.Run(() => _apiController.GroupGetBannedUsers(new GroupDto(group.Group)));
            }
        }

        DrawOperationStatus(_banRefreshOperation, "Refreshing banlist...");
        if (!string.IsNullOrWhiteSpace(_banStatus))
        {
            ElezenImgui.ColouredWrappedText(_banStatus, ImGuiColors.DalamudYellow);
        }

        if (ImGui.BeginTable("bannedusertable" + group.GID, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("UID", ImGuiTableColumnFlags.None, 1);
            ImGui.TableSetupColumn("Alias", ImGuiTableColumnFlags.None, 1);
            ImGui.TableSetupColumn("By", ImGuiTableColumnFlags.None, 1);
            ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.None, 2);
            ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.None, 3);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.None, 1);
            ImGui.TableHeadersRow();

            foreach (var bannedUser in _bannedUsers)
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
                ElezenImgui.WrappedText(bannedUser.Reason);
                ImGui.TableNextColumn();
                DrawUnbanAction(bannedUser);
            }

            ImGui.EndTable();
        }
    }

    private static IEnumerable<MemberRow> BuildRows(GroupFullInfoDto group, IEnumerable<Pair> pairs)
    {
        return pairs
            .Select(pair =>
            {
                pair.GroupPair.TryGetValue(group, out var memberInfo);
                var userInfo = memberInfo?.GroupPairStatusInfo ?? GroupUserInfo.None;
                return new MemberRow(pair, memberInfo, userInfo);
            })
            .OrderBy(row =>
            {
                if (row.UserInfo.IsModerator()) return 0;
                if (row.UserInfo.IsPinned()) return 1;
                return 10;
            })
            .ThenBy(row => row.Pair.GetNote() ?? row.Pair.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase);
    }

    private void DrawMemberRow(GroupFullInfoDto group, MemberRow row, bool isOwner, bool isModerator, Action<GroupPairFullInfoDto> openMemberLabelEditor)
    {
        using var tableId = ImRaii.PushId("userTable_" + row.Pair.UserData.UID);
        var pair = row.Pair;
        var memberInfo = row.MemberInfo;
        var userInfo = row.UserInfo;
        var entryIsMod = userInfo.IsModerator();
        var entryIsOwner = string.Equals(pair.UserData.UID, group.OwnerUID, StringComparison.Ordinal);
        var canModerateMember = isOwner || (isModerator && !entryIsMod && !entryIsOwner);
        var op = GetMemberOperation(pair.UserData.UID);

        ImGui.TableNextColumn();
        var note = pair.GetNote();
        var text = note == null ? pair.UserData.AliasOrUID : note + " (" + pair.UserData.AliasOrUID + ")";
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(text);

        ImGui.TableNextColumn();
        var onlineText = pair.IsOnline ? "Online" : "Offline";
        var name = pair.GetNoteOrName();
        if (!string.IsNullOrEmpty(name))
        {
            onlineText += " (" + name + ")";
        }

        ImGui.AlignTextToFramePadding();
        ElezenImgui.ColouredText(onlineText, ElezenImgui.GetBooleanColour(pair.IsOnline));

        ImGui.TableNextColumn();
        if (memberInfo != null && memberInfo.MemberLabels.Count > 0)
        {
            ElezenImgui.WrappedText(SyncshellMemberLabelUi.FormatLabels(memberInfo.MemberLabels));
        }
        else
        {
            ElezenImgui.ColouredText("None", ImGuiColors.DalamudGrey);
        }

        ImGui.TableNextColumn();
        DrawMemberFlags(userInfo);

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(op.IsRunning))
        {
            DrawMemberActions(group, pair, memberInfo, userInfo, isOwner, canModerateMember, entryIsOwner, openMemberLabelEditor);
        }
        DrawOperationStatus(op, "Saving...");
    }

    private static void DrawMemberFlags(GroupUserInfo userInfo)
    {
        var drewFlag = false;
        if (userInfo.IsModerator())
        {
            ElezenImgui.ShowIcon(FontAwesomeIcon.UserShield);
            ElezenImgui.AttachTooltip("Moderator");
            drewFlag = true;
        }

        if (userInfo.IsPinned())
        {
            if (drewFlag)
            {
                ImGui.SameLine();
            }

            ElezenImgui.ShowIcon(FontAwesomeIcon.Thumbtack);
            ElezenImgui.AttachTooltip("Pinned");
            drewFlag = true;
        }

        if (!drewFlag)
        {
            ElezenImgui.ShowIcon(FontAwesomeIcon.None);
        }
    }

    private void DrawMemberActions(GroupFullInfoDto group, Pair pair, GroupPairFullInfoDto? memberInfo, GroupUserInfo userInfo,
        bool isOwner, bool canModerateMember, bool entryIsOwner, Action<GroupPairFullInfoDto> openMemberLabelEditor)
    {
        if (isOwner && !entryIsOwner)
        {
            if (ElezenImgui.IconButton(FontAwesomeIcon.UserShield))
            {
                var updated = userInfo;
                updated.SetModerator(!updated.IsModerator());
                StartMemberOperation(pair.UserData.UID, () => _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(group.Group, pair.UserData, updated)));
            }
            ElezenImgui.AttachTooltip(userInfo.IsModerator() ? "Demod user" : "Mod user");
            ImGui.SameLine();
        }

        if (memberInfo != null)
        {
            if (ElezenImgui.IconButton(FontAwesomeIcon.IdBadge))
            {
                openMemberLabelEditor(memberInfo);
            }
            ElezenImgui.AttachTooltip("Edit shared syncshell roles for this member.");
            ImGui.SameLine();
        }

        if (canModerateMember)
        {
            if (ElezenImgui.IconButton(FontAwesomeIcon.Thumbtack))
            {
                var updated = userInfo;
                updated.SetPinned(!updated.IsPinned());
                StartMemberOperation(pair.UserData.UID, () => _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(group.Group, pair.UserData, updated)));
            }
            ElezenImgui.AttachTooltip(userInfo.IsPinned() ? "Unpin user" : "Pin user");
            ImGui.SameLine();

            using (ImRaii.Disabled(!ElezenImgui.CtrlPressed()))
            {
                if (ElezenImgui.IconButton(FontAwesomeIcon.Trash))
                {
                    StartMemberOperation(pair.UserData.UID, () => _apiController.GroupRemoveUser(new GroupPairDto(group.Group, pair.UserData)));
                }
            }
            ElezenImgui.AttachTooltip("Remove user from Syncshell"
                                      + ElezenImgui.TooltipSeparator + "Hold CTRL to enable this button");

            ImGui.SameLine();
            using (ImRaii.Disabled(!ElezenImgui.CtrlPressed()))
            {
                if (ElezenImgui.IconButton(FontAwesomeIcon.Ban))
                {
                    _mediator.Publish(new OpenBanUserPopupMessage(pair, group));
                }
            }
            ElezenImgui.AttachTooltip("Ban user from Syncshell"
                                      + ElezenImgui.TooltipSeparator + "Hold CTRL to enable this button");
            ImGui.SameLine();
        }

        if (isOwner && memberInfo != null && !entryIsOwner)
        {
            using (ImRaii.Disabled(!ElezenImgui.CtrlPressed() || !ElezenImgui.ShiftPressed()))
            {
                if (ElezenImgui.IconButton(FontAwesomeIcon.Crown))
                {
                    StartMemberOperation(pair.UserData.UID, () => _apiController.GroupChangeOwnership(memberInfo));
                }
            }
            ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "Hold CTRL and SHIFT and click to transfer ownership of this Syncshell to {0}", memberInfo.UserAliasOrUID)
                                      + Environment.NewLine + "WARNING: This action is irreversible.");
        }
    }

    private void DrawPruneStatus(GroupFullInfoDto group)
    {
        if (_prunePreviewOperation.IsRunning)
        {
            ElezenImgui.ColouredWrappedText("Calculating inactive users...", ImGuiColors.DalamudYellow);
            return;
        }

        if (_prunePreviewOperation.Faulted)
        {
            ElezenImgui.ColouredWrappedText(_prunePreviewOperation.Error ?? "Failed to calculate inactive users.", ImGuiColors.DalamudRed);
            return;
        }

        if (_prunePreviewOperation.IsCompleted)
        {
            var inactiveCount = _prunePreviewOperation.Result;
            ImGui.AlignTextToFramePadding();
            ElezenImgui.WrappedText(string.Format(CultureInfo.CurrentCulture, "Found {0} user(s) that have not logged in the past {1} days.", inactiveCount, _pruneDays));
            if (inactiveCount > 0)
            {
                using (ImRaii.Disabled(!ElezenImgui.CtrlPressed() || _pruneOperation.IsRunning))
                {
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Broom, "Prune Inactive Users"))
                    {
                        _ = _pruneOperation.Run(() => _apiController.GroupPrune(new(group.Group), _pruneDays, execute: true));
                    }
                }
                ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "Pruning will remove {0} inactive user(s).", inactiveCount)
                                          + ElezenImgui.TooltipSeparator + "Hold CTRL to enable this button");
            }
        }

        if (_pruneOperation.IsRunning)
        {
            ElezenImgui.ColouredWrappedText("Pruning Syncshell...", ImGuiColors.DalamudYellow);
        }
        else if (_pruneOperation.Faulted)
        {
            ElezenImgui.ColouredWrappedText(_pruneOperation.Error ?? "Failed to prune Syncshell.", ImGuiColors.DalamudRed);
        }
        else if (_pruneOperation.IsCompleted)
        {
            ElezenImgui.WrappedText(string.Format(CultureInfo.CurrentCulture, "Syncshell was pruned and {0} inactive user(s) have been removed.", _pruneOperation.Result));
        }
    }

    private void DrawUnbanAction(BannedGroupUserDto bannedUser)
    {
        var op = GetUnbanOperation(bannedUser.UID);
        using var pushId = ImRaii.PushId(bannedUser.UID);
        using (ImRaii.Disabled(op.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Check, "Unban"))
            {
                _ = op.Run(async () =>
                {
                    await _apiController.GroupUnbanUser(bannedUser).ConfigureAwait(false);
                    return true;
                });
            }
        }

        DrawOperationStatus(op, "Unbanning...");
    }

    private void ConsumeBanRefresh()
    {
        if (!_banRefreshOperation.IsCompleted)
        {
            return;
        }

        if (_banRefreshOperation.Faulted)
        {
            _banStatus = _banRefreshOperation.Error ?? "Failed to refresh banlist.";
        }
        else
        {
            _bannedUsers = _banRefreshOperation.Result ?? [];
            _banStatus = _bannedUsers.Count == 0 ? "No banned users found." : string.Empty;
        }

        _banRefreshOperation.Reset();
    }

    private void ConsumeUnbans()
    {
        foreach (var entry in _unbanOperations.Where(kvp => kvp.Value.IsCompleted && !kvp.Value.Faulted && kvp.Value.Result).ToList())
        {
            _bannedUsers.RemoveAll(user => string.Equals(user.UID, entry.Key, StringComparison.Ordinal));
            _unbanOperations.Remove(entry.Key);
        }
    }

    private AsyncOp GetMemberOperation(string uid)
    {
        if (!_memberOperations.TryGetValue(uid, out var op))
        {
            op = new AsyncOp();
            _memberOperations[uid] = op;
        }

        return op;
    }

    private AsyncOp<bool> GetUnbanOperation(string uid)
    {
        if (!_unbanOperations.TryGetValue(uid, out var op))
        {
            op = new AsyncOp<bool>();
            _unbanOperations[uid] = op;
        }

        return op;
    }

    private void StartMemberOperation(string uid, Func<Task> operation)
    {
        var op = GetMemberOperation(uid);
        if (!op.IsRunning)
        {
            _ = op.Run(operation);
        }
    }

    private static void DrawOperationStatus(AsyncOp op, string runningText)
    {
        if (op.IsRunning)
        {
            ImGui.SameLine();
            ElezenImgui.ColouredText(runningText, ImGuiColors.DalamudYellow);
        }
        else if (op.Faulted)
        {
            ImGui.SameLine();
            ElezenImgui.ColouredText(op.Error ?? "Failed", ImGuiColors.DalamudRed);
        }
    }

    private readonly record struct MemberRow(Pair Pair, GroupPairFullInfoDto? MemberInfo, GroupUserInfo UserInfo);
}
