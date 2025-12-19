using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System.Globalization;
using Snowcloak.Services.Localisation;


namespace Snowcloak.UI;

public class SyncshellAdminUI : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly bool _isModerator = false;
    private readonly bool _isOwner = false;
    private readonly List<string> _oneTimeInvites = [];
    private readonly PairManager _pairManager;
    private readonly UiSharedService _uiSharedService;
    private readonly LocalisationService _localisationService;
    private List<BannedGroupUserDto> _bannedUsers = [];
    private int _multiInvites;
    private string _newPassword;
    private bool _pwChangeSuccess;
    private Task<int>? _pruneTestTask;
    private Task<int>? _pruneTask;
    private int _pruneDays = 14;

    public SyncshellAdminUI(ILogger<SyncshellAdminUI> logger, SnowMediator mediator, ApiController apiController,
        UiSharedService uiSharedService, PairManager pairManager, GroupFullInfoDto groupFullInfo, PerformanceCollectorService performanceCollectorService,
        LocalisationService localisationService)
        : base(logger, mediator, string.Format(localisationService.GetString("SyncshellAdminUI.WindowTitle", "Syncshell Admin Panel ({0})"), groupFullInfo.GroupAliasOrGID), performanceCollectorService)
    {
        GroupFullInfo = groupFullInfo;
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _localisationService = localisationService;
        _pairManager = pairManager;
        _isOwner = string.Equals(GroupFullInfo.OwnerUID, _apiController.UID, StringComparison.Ordinal);
        _isModerator = GroupFullInfo.GroupUserInfo.IsModerator();
        _newPassword = string.Empty;
        _multiInvites = 30;
        _pwChangeSuccess = true;
        IsOpen = true;
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(700, 500),
            MaximumSize = new(700, 2000),
        };
    }

    public GroupFullInfoDto GroupFullInfo { get; private set; }

    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"GroupPanel.{key}", fallback);
    }
    
    protected override void DrawInternal()
    {
        if (!_isModerator && !_isOwner) return;

        GroupFullInfo = _pairManager.Groups[GroupFullInfo.Group];

        using var id = ImRaii.PushId("syncshell_admin_" + GroupFullInfo.GID);

        using (_uiSharedService.UidFont.Push())
            ImGui.TextUnformatted(string.Format(L("AdminPanelHeader", "{0} Administrative Panel"), GroupFullInfo.GroupAliasOrGID));
        ImGui.Separator();
        var perm = GroupFullInfo.GroupPermissions;

        using var tabbar = ImRaii.TabBar("syncshell_tab_" + GroupFullInfo.GID);

        if (tabbar)
        {
            var inviteTab = ImRaii.TabItem(L("TabInvites", "Invites"));
            if (inviteTab)
            {
                bool isInvitesDisabled = perm.IsDisableInvites();

                if (_uiSharedService.IconTextButton(isInvitesDisabled ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock,
                        isInvitesDisabled ? L("UnlockSyncshell", "Unlock Syncshell") : L("LockSyncshell", "Lock Syncshell")))
                {
                    perm.SetDisableInvites(!isInvitesDisabled);
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGuiHelpers.ScaledDummy(2f);

                UiSharedService.TextWrapped(L("OneTimeInviteExplanation", "One-time invites work as single-use passwords. Use those if you do not want to distribute your Syncshell password."));
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Envelope, L("SingleOneTimeInvite", "Single one-time invite")))
                {
                    ImGui.SetClipboardText(_apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), 1).Result.FirstOrDefault() ?? string.Empty);
                }
                UiSharedService.AttachToolTip(L("SingleOneTimeInviteTooltip", "Creates a single-use password for joining the syncshell which is valid for 24h and copies it to the clipboard."));
                ImGui.InputInt("##amountofinvites", ref _multiInvites);
                ImGui.SameLine();
                using (ImRaii.Disabled(_multiInvites <= 1 || _multiInvites > 100))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Envelope, string.Format(L("GenerateOneTimeInvites", "Generate {0} one-time invites"), _multiInvites)))
                    {
                        _oneTimeInvites.AddRange(_apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), _multiInvites).Result);
                    }
                }

                if (_oneTimeInvites.Any())
                {
                    var invites = string.Join(Environment.NewLine, _oneTimeInvites);
                    ImGui.InputTextMultiline(L("GeneratedInvitesLabel", "Generated Multi Invites"), ref invites, 5000, new(0, 0), ImGuiInputTextFlags.ReadOnly);
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Copy, L("CopyInvitesToClipboard", "Copy Invites to clipboard")))
                    {
                        ImGui.SetClipboardText(invites);
                    }
                }
            }
            inviteTab.Dispose();

            var mgmtTab = ImRaii.TabItem(L("TabUserManagement", "User Management"));
            if (mgmtTab)
            {
                var userNode = ImRaii.TreeNode(L("UserListAdministration", "User List & Administration"));
                if (userNode)
                {
                    if (!_pairManager.GroupPairs.TryGetValue(GroupFullInfo, out var pairs))
                    {
                        UiSharedService.ColorTextWrapped(L("NoUsersFound", "No users found in this Syncshell"), ImGuiColors.DalamudYellow);
                    }
                    else
                    {
                        using var table = ImRaii.Table("userList#" + GroupFullInfo.Group.GID, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY);
                        if (table)
                        {
                            ImGui.TableSetupColumn(L("ColumnAlias", "Alias/UID/Note"), ImGuiTableColumnFlags.None, 3);
                            ImGui.TableSetupColumn(L("ColumnOnline", "Online/Name"), ImGuiTableColumnFlags.None, 2);
                            ImGui.TableSetupColumn(L("ColumnFlags", "Flags"), ImGuiTableColumnFlags.None, 1);
                            ImGui.TableSetupColumn(L("ColumnActions", "Actions"), ImGuiTableColumnFlags.None, 2);
                            ImGui.TableHeadersRow();

                            var groupedPairs = new Dictionary<Pair, GroupUserInfo?>(pairs.Select(p => new KeyValuePair<Pair, GroupUserInfo?>(p,
                                p.GroupPair.TryGetValue(GroupFullInfo, out GroupPairFullInfoDto? value) ? value.GroupPairStatusInfo : null)));

                            foreach (var pair in groupedPairs.OrderBy(p =>
                            {
                                if (p.Value == null) return 10;
                                if (p.Value.Value.IsModerator()) return 0;
                                if (p.Value.Value.IsPinned()) return 1;
                                return 10;
                            }).ThenBy(p => p.Key.GetNote() ?? p.Key.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase))
                            {
                                using var tableId = ImRaii.PushId("userTable_" + pair.Key.UserData.UID);

                                ImGui.TableNextColumn(); // alias/uid/note
                                var note = pair.Key.GetNote();
                                var text = note == null ? pair.Key.UserData.AliasOrUID : note + " (" + pair.Key.UserData.AliasOrUID + ")";
                                ImGui.AlignTextToFramePadding();
                                ImGui.TextUnformatted(text);

                                ImGui.TableNextColumn(); // online/name
                                string onlineText = pair.Key.IsOnline ? L("Online", "Online") : L("Offline", "Offline");
                                string? name = pair.Key.GetNoteOrName();
                                if (!string.IsNullOrEmpty(name))
                                {
                                    onlineText += " (" + name + ")";
                                }
                                var boolcolor = UiSharedService.GetBoolColor(pair.Key.IsOnline);
                                ImGui.AlignTextToFramePadding();
                                UiSharedService.ColorText(onlineText, boolcolor);

                                ImGui.TableNextColumn(); // special flags
                                if (pair.Value != null && (pair.Value.Value.IsModerator() || pair.Value.Value.IsPinned()))
                                {
                                    if (pair.Value.Value.IsModerator())
                                    {
                                        _uiSharedService.IconText(FontAwesomeIcon.UserShield);
                                        UiSharedService.AttachToolTip(L("Moderator", "Moderator"));
                                    }
                                    if (pair.Value.Value.IsPinned())
                                    {
                                        _uiSharedService.IconText(FontAwesomeIcon.Thumbtack);
                                        UiSharedService.AttachToolTip(L("Pinned", "Pinned"));
                                    }
                                }
                                else
                                {
                                    _uiSharedService.IconText(FontAwesomeIcon.None);
                                }

                                ImGui.TableNextColumn(); // actions
                                if (_isOwner)
                                {
                                    if (_uiSharedService.IconButton(FontAwesomeIcon.UserShield))
                                    {
                                        GroupUserInfo userInfo = pair.Value ?? GroupUserInfo.None;

                                        userInfo.SetModerator(!userInfo.IsModerator());

                                        _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(GroupFullInfo.Group, pair.Key.UserData, userInfo));
                                    }
                                    UiSharedService.AttachToolTip(pair.Value != null && pair.Value.Value.IsModerator() ? L("DemoteUser", "Demod user") : L("PromoteUser", "Mod user"));
                                    ImGui.SameLine();
                                }

                                if (_isOwner || (pair.Value == null || (pair.Value != null && !pair.Value.Value.IsModerator())))
                                {
                                    if (_uiSharedService.IconButton(FontAwesomeIcon.Thumbtack))
                                    {
                                        GroupUserInfo userInfo = pair.Value ?? GroupUserInfo.None;

                                        userInfo.SetPinned(!userInfo.IsPinned());

                                        _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(GroupFullInfo.Group, pair.Key.UserData, userInfo));
                                    }
                                    UiSharedService.AttachToolTip(pair.Value != null && pair.Value.Value.IsPinned() ? L("UnpinUser", "Unpin user") : L("PinUser", "Pin user"));
                                    ImGui.SameLine();

                                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                    {
                                        if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                                        {
                                            _ = _apiController.GroupRemoveUser(new GroupPairDto(GroupFullInfo.Group, pair.Key.UserData));
                                        }
                                    }
                                    UiSharedService.AttachToolTip(L("RemoveUserTooltip", "Remove user from Syncshell")
                                                                  + UiSharedService.TooltipSeparator + L("CtrlEnableHint", "Hold CTRL to enable this button"));

                                    ImGui.SameLine();
                                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                    {
                                        if (_uiSharedService.IconButton(FontAwesomeIcon.Ban))
                                        {
                                            Mediator.Publish(new OpenBanUserPopupMessage(pair.Key, GroupFullInfo));
                                        }
                                    }
                                    UiSharedService.AttachToolTip(L("BanUserTooltip", "Ban user from Syncshell")
                                                                  + UiSharedService.TooltipSeparator + L("CtrlEnableHint", "Hold CTRL to enable this button"));
                                }
                            }
                        }
                    }
                }
                userNode.Dispose();
                var clearNode = ImRaii.TreeNode(L("MassCleanup", "Mass Cleanup"));
                if (clearNode)
                {
                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Broom, L("ClearSyncshell", "Clear Syncshell")))
                        {
                            _ = _apiController.GroupClear(new(GroupFullInfo.Group));
                        }
                    }
                    UiSharedService.AttachToolTip(L("ClearSyncshellTooltip", "This will remove all non-pinned, non-moderator users from the Syncshell.")
                                                  + UiSharedService.TooltipSeparator + L("CtrlEnableHint", "Hold CTRL to enable this button"));

                    ImGuiHelpers.ScaledDummy(2f);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(2f);

                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Unlink, L("CheckInactiveUsers", "Check for Inactive Users")))
                    {
                        _pruneTestTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: false);
                        _pruneTask = null;
                    }
                    UiSharedService.AttachToolTip(string.Format(L("CheckInactiveUsersTooltip", "This will start the prune process for this Syncshell of inactive users that have not logged in the past {0} days."), _pruneDays)
                                                  + Environment.NewLine + L("InactiveUsersReview", "You will be able to review the amount of inactive users before executing the prune.")
                                                  + UiSharedService.TooltipSeparator + L("PruneExcludesNote", "Note: pruning excludes pinned users and moderators of this Syncshell."));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150);
                    _uiSharedService.DrawCombo(L("DaysOfInactivity", "Days of inactivity"), [7, 14, 30, 90], (count) =>
                    {
                        return string.Format(L("DaysEntry", "{0} days"), count);
                    },
                    (selected) =>
                    {
                        _pruneDays = selected;
                        _pruneTestTask = null;
                        _pruneTask = null;
                    },
                    _pruneDays);

                    if (_pruneTestTask != null)
                    {
                        if (!_pruneTestTask.IsCompleted)
                        {
                            UiSharedService.ColorTextWrapped(L("CalculatingInactive", "Calculating inactive users..."), ImGuiColors.DalamudYellow);
                        }
                        else
                        {
                            ImGui.AlignTextToFramePadding();
                            UiSharedService.TextWrapped(string.Format(L("InactiveUsersFound", "Found {0} user(s) that have not logged in the past {1} days."), _pruneTestTask.Result, _pruneDays));
                            if (_pruneTestTask.Result > 0)
                            {
                                using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                {
                                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Broom, L("PruneInactiveUsers", "Prune Inactive Users")))
                                    {
                                        _pruneTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: true);
                                        _pruneTestTask = null;
                                    }
                                }
                                UiSharedService.AttachToolTip(string.Format(L("PruneWillRemove", "Pruning will remove {0} inactive user(s)."), _pruneTestTask?.Result ?? 0)
                                                              + UiSharedService.TooltipSeparator + L("CtrlEnableHint", "Hold CTRL to enable this button"));
                            }
                        }
                    }
                    if (_pruneTask != null)
                    {
                        if (!_pruneTask.IsCompleted)
                        {
                            UiSharedService.ColorTextWrapped(L("PruningSyncshell", "Pruning Syncshell..."), ImGuiColors.DalamudYellow);
                        }
                        else
                        {
                            UiSharedService.TextWrapped(string.Format(L("PruneComplete", "Syncshell was pruned and {0} inactive user(s) have been removed."), _pruneTask.Result));
                        }
                    }
                }
                clearNode.Dispose();

                var banNode = ImRaii.TreeNode(L("UserBans", "User Bans"));
                if (banNode)
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Retweet, L("RefreshBanlist", "Refresh Banlist from Server")))
                    {
                        _bannedUsers = _apiController.GroupGetBannedUsers(new GroupDto(GroupFullInfo.Group)).Result;
                    }

                    if (ImGui.BeginTable("bannedusertable" + GroupFullInfo.GID, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
                    {
                        ImGui.TableSetupColumn(L("BanColumnUid", "UID"), ImGuiTableColumnFlags.None, 1);
                        ImGui.TableSetupColumn(L("BanColumnAlias", "Alias"), ImGuiTableColumnFlags.None, 1);
                        ImGui.TableSetupColumn(L("BanColumnBy", "By"), ImGuiTableColumnFlags.None, 1);
                        ImGui.TableSetupColumn(L("BanColumnDate", "Date"), ImGuiTableColumnFlags.None, 2);
                        ImGui.TableSetupColumn(L("BanColumnReason", "Reason"), ImGuiTableColumnFlags.None, 3);
                        ImGui.TableSetupColumn(L("BanColumnActions", "Actions"), ImGuiTableColumnFlags.None, 1);

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
                            using var pushId = ImRaii.PushId(bannedUser.UID);
                            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Check, L("Unban", "Unban")))
                            {
                                _ = Task.Run(async () => await _apiController.GroupUnbanUser(bannedUser).ConfigureAwait(false));
                                _bannedUsers.RemoveAll(b => string.Equals(b.UID, bannedUser.UID, StringComparison.Ordinal));
                            }
                        }

                        ImGui.EndTable();
                    }
                }
                banNode.Dispose();
            }
            mgmtTab.Dispose();

            var permissionTab = ImRaii.TabItem(L("TabPermissions", "Permissions"));
            if (permissionTab)
            {
                bool isDisableAnimations = perm.IsDisableAnimations();
                bool isDisableSounds = perm.IsDisableSounds();
                bool isDisableVfx = perm.IsDisableVFX();

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(L("SoundSync", "Sound Sync"));
                _uiSharedService.BooleanToColoredIcon(!isDisableSounds);
                ImGui.SameLine(230);
                if (_uiSharedService.IconTextButton(isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute,
                        isDisableSounds ? L("EnableSoundSync", "Enable sound sync") : L("DisableSoundSync", "Disable sound sync")))
                {
                    perm.SetDisableSounds(!perm.IsDisableSounds());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(L("AnimationSync", "Animation Sync"));
                _uiSharedService.BooleanToColoredIcon(!isDisableAnimations);
                ImGui.SameLine(230);
                if (_uiSharedService.IconTextButton(isDisableAnimations ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop,
                        isDisableAnimations ? L("EnableAnimationSync", "Enable animation sync") : L("DisableAnimationSync", "Disable animation sync")))
                {
                    perm.SetDisableAnimations(!perm.IsDisableAnimations());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(L("VfxSync", "VFX Sync"));
                _uiSharedService.BooleanToColoredIcon(!isDisableVfx);
                ImGui.SameLine(230);
                if (_uiSharedService.IconTextButton(isDisableVfx ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle,
                        isDisableVfx ? L("EnableVfxSync", "Enable VFX sync") : L("DisableVfxSync", "Disable VFX sync")))
                {
                    perm.SetDisableVFX(!perm.IsDisableVFX());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }
            }
            permissionTab.Dispose();

            if (_isOwner)
            {
                var ownerTab = ImRaii.TabItem(L("TabOwnerSettings", "Owner Settings"));
                if (ownerTab)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(L("NewPassword", "New Password"));
                    var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                    var buttonSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Passport, L("ChangePassword", "Change Password"));
                    var textSize = ImGui.CalcTextSize(L("NewPassword", "New Password")).X;
                    var spacing = ImGui.GetStyle().ItemSpacing.X;

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(availableWidth - buttonSize - textSize - spacing * 2);
                    ImGui.InputTextWithHint("##changepw", L("PasswordHint", "Min 10 characters"), ref _newPassword, 50);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(_newPassword.Length < 10))
                    {
                        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Passport, L("ChangePassword", "Change Password")))
                        {
                            _pwChangeSuccess = _apiController.GroupChangePassword(new GroupPasswordDto(GroupFullInfo.Group, _newPassword)).Result;
                            _newPassword = string.Empty;
                        }
                    }
                    UiSharedService.AttachToolTip(L("PasswordRequirement", "Password requires to be at least 10 characters long. This action is irreversible."));
                    
                    if (!_pwChangeSuccess)
                    {
                        UiSharedService.ColorTextWrapped(L("PasswordChangeFailed", "Failed to change the password. Password requires to be at least 10 characters long."), ImGuiColors.DalamudYellow);
                    }

                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, L("DeleteSyncshell", "Delete Syncshell")) && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
                    {
                        IsOpen = false;
                        _ = _apiController.GroupDelete(new(GroupFullInfo.Group));
                    }
                    UiSharedService.AttachToolTip(L("DeleteSyncshellTooltip", "Hold CTRL and Shift and click to delete this Syncshell.") + Environment.NewLine + L("DeleteWarning", "WARNING: this action is irreversible."));
                }
                ownerTab.Dispose();
            }
        }
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}
