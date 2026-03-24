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
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Snowcloak.UI;

public class SyncshellAdminUI : WindowMediatorSubscriberBase
{
    private const int AuditPageSize = 50;
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _configService;
    private readonly bool _isModerator = false;
    private readonly bool _isOwner = false;
    private readonly List<string> _oneTimeInvites = [];
    private readonly PairManager _pairManager;
    private readonly SyncshellBudgetPanel _syncshellBudgetPanel;
    private readonly UiSharedService _uiSharedService;
    private GroupAuditAction? _auditActionFilter;
    private List<GroupAuditEntryDto> _auditEntries = [];
    private Task<GroupAuditPageDto>? _auditLogTask;
    private string _auditSearch = string.Empty;
    private int _auditSkip;
    private int _auditTotalCount;
    private List<BannedGroupUserDto> _bannedUsers = [];
    private int _multiInvites;
    private string _newPassword;
    private GroupPairFullInfoDto? _memberLabelEditorTarget;
    private List<string> _memberLabelDraft = [];
    private string _memberLabelError = string.Empty;
    private bool _memberLabelEditorPopupPendingOpen;
    private bool _pwChangeSuccess;
    private Task<int>? _pruneTestTask;
    private Task<int>? _pruneTask;
    private int _pruneDays = 14;
    private bool _showMemberLabelEditor;

    public SyncshellAdminUI(ILogger<SyncshellAdminUI> logger, SnowMediator mediator, ApiController apiController,
        SnowcloakConfigService configService,
        UiSharedService uiSharedService, PairManager pairManager, GroupFullInfoDto groupFullInfo, PerformanceCollectorService performanceCollectorService,
        SyncshellBudgetService syncshellBudgetService)
        : base(logger, mediator, string.Format("Syncshell Admin Panel ({0})", groupFullInfo.GroupAliasOrGID), performanceCollectorService)
    {
        GroupFullInfo = groupFullInfo;
        _apiController = apiController;
        _configService = configService;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _syncshellBudgetPanel = new(syncshellBudgetService);
        _isOwner = string.Equals(GroupFullInfo.OwnerUID, _apiController.UID, StringComparison.Ordinal);
        _isModerator = GroupFullInfo.GroupUserInfo.IsModerator();
        _newPassword = string.Empty;
        _multiInvites = 30;
        _pwChangeSuccess = true;
        IsOpen = true;
        RequestAuditLogPage(0);
        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new(700, 500),
            MaximumSize = new(700, 2000),
        };
    }

    public GroupFullInfoDto GroupFullInfo { get; private set; }
    
    protected override void DrawInternal()
    {
        if (!_isModerator && !_isOwner) return;
        ConsumeAuditLogTask();

        GroupFullInfo = _pairManager.Groups[GroupFullInfo.Group];

        using var id = ImRaii.PushId("syncshell_admin_" + GroupFullInfo.GID);

        using (_uiSharedService.UidFont.Push())
            ImGui.TextUnformatted(string.Format("{0} Administrative Panel", GroupFullInfo.GroupAliasOrGID));
        ImGui.Separator();
        var perm = GroupFullInfo.GroupPermissions;

        using var tabbar = ImRaii.TabBar("syncshell_tab_" + GroupFullInfo.GID);

        if (tabbar)
        {
            if (_configService.Current.ShowSyncshellBudgetDashboard)
            {
                var budgetTab = ImRaii.TabItem("Performance");
                if (budgetTab)
                {
                    if (_pairManager.GroupPairs.TryGetValue(GroupFullInfo, out var budgetPairs))
                    {
                        _syncshellBudgetPanel.Draw(GroupFullInfo, budgetPairs
                            .Where(p => !string.Equals(p.UserData.UID, _apiController.UID, StringComparison.Ordinal))
                            .ToList());
                    }
                    else
                    {
                        _syncshellBudgetPanel.Draw(GroupFullInfo, []);
                    }
                }
                budgetTab.Dispose();
            }

            var inviteTab = ImRaii.TabItem("Invites");
            if (inviteTab)
            {
                bool isInvitesDisabled = perm.IsDisableInvites();

                if (ElezenImgui.ShowIconButton(isInvitesDisabled ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock,
                        isInvitesDisabled ? "Unlock Syncshell" : "Lock Syncshell"))
                {
                    perm.SetDisableInvites(!isInvitesDisabled);
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGuiHelpers.ScaledDummy(2f);

                ElezenImgui.WrappedText("One-time invites work as single-use passwords. Use those if you do not want to distribute your Syncshell password.");
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Envelope, "Single one-time invite"))
                {
                    ImGui.SetClipboardText(_apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), 1).Result.FirstOrDefault() ?? string.Empty);
                }
                ElezenImgui.AttachTooltip("Creates a single-use password for joining the syncshell which is valid for 24h and copies it to the clipboard.");
                ImGui.InputInt("##amountofinvites", ref _multiInvites);
                ImGui.SameLine();
                using (ImRaii.Disabled(_multiInvites <= 1 || _multiInvites > 100))
                {
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Envelope, string.Format("Generate {0} one-time invites", _multiInvites)))
                    {
                        _oneTimeInvites.AddRange(_apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), _multiInvites).Result);
                    }
                }

                if (_oneTimeInvites.Any())
                {
                    var invites = string.Join(Environment.NewLine, _oneTimeInvites);
                    ImGui.InputTextMultiline("Generated Multi Invites", ref invites, 5000, new(0, 0), ImGuiInputTextFlags.ReadOnly);
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Copy, "Copy Invites to clipboard"))
                    {
                        ImGui.SetClipboardText(invites);
                    }
                }
            }
            inviteTab.Dispose();

            var mgmtTab = ImRaii.TabItem("User Management");
            if (mgmtTab)
            {
                var userNode = ImRaii.TreeNode("User List & Administration");
                if (userNode)
                {
                    if (!_pairManager.GroupPairs.TryGetValue(GroupFullInfo, out var pairs))
                    {
                        ElezenImgui.ColouredWrappedText("No users found in this Syncshell", ImGuiColors.DalamudYellow);
                    }
                    else
                    {
                        using var table = ImRaii.Table("userList#" + GroupFullInfo.Group.GID, 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY);
                        if (table)
                        {
                            ImGui.TableSetupColumn("Alias/UID/Note", ImGuiTableColumnFlags.None, 3);
                            ImGui.TableSetupColumn("Online/Name", ImGuiTableColumnFlags.None, 2);
                            ImGui.TableSetupColumn("Roles", ImGuiTableColumnFlags.None, 2);
                            ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.None, 1);
                            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.None, 2);
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
                                pair.Key.GroupPair.TryGetValue(GroupFullInfo, out GroupPairFullInfoDto? memberInfo);

                                ImGui.TableNextColumn(); // alias/uid/note
                                var note = pair.Key.GetNote();
                                var text = note == null ? pair.Key.UserData.AliasOrUID : note + " (" + pair.Key.UserData.AliasOrUID + ")";
                                ImGui.AlignTextToFramePadding();
                                ImGui.TextUnformatted(text);

                                ImGui.TableNextColumn(); // online/name
                                string onlineText = pair.Key.IsOnline ?"Online" :"Offline";
                                string? name = pair.Key.GetNoteOrName();
                                if (!string.IsNullOrEmpty(name))
                                {
                                    onlineText += " (" + name + ")";
                                }
                                var boolcolor = ElezenImgui.GetBooleanColour(pair.Key.IsOnline);
                                ImGui.AlignTextToFramePadding();
                                ElezenImgui.ColouredText(onlineText, boolcolor);

                                ImGui.TableNextColumn(); // roles
                                if (memberInfo != null && memberInfo.MemberLabels.Count > 0)
                                {
                                    ElezenImgui.WrappedText(SyncshellMemberLabelUi.FormatLabels(memberInfo.MemberLabels));
                                }
                                else
                                {
                                    ElezenImgui.ColouredText("None", ImGuiColors.DalamudGrey);
                                }

                                ImGui.TableNextColumn(); // special flags
                                if (pair.Value != null && (pair.Value.Value.IsModerator() || pair.Value.Value.IsPinned()))
                                {
                                    if (pair.Value.Value.IsModerator())
                                    {
                                        ElezenImgui.ShowIcon(FontAwesomeIcon.UserShield);
                                        ElezenImgui.AttachTooltip("Moderator");
                                    }
                                    if (pair.Value.Value.IsPinned())
                                    {
                                        ElezenImgui.ShowIcon(FontAwesomeIcon.Thumbtack);
                                        ElezenImgui.AttachTooltip("Pinned");
                                    }
                                }
                                else
                                {
                                    ElezenImgui.ShowIcon(FontAwesomeIcon.None);
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
                                    ElezenImgui.AttachTooltip(pair.Value != null && pair.Value.Value.IsModerator() ? "Demod user" : "Mod user");
                                    ImGui.SameLine();
                                }

                                if (memberInfo != null && _uiSharedService.IconButton(FontAwesomeIcon.IdBadge))
                                {
                                    OpenMemberLabelEditor(memberInfo);
                                }
                                if (memberInfo != null)
                                {
                                    ElezenImgui.AttachTooltip("Edit shared syncshell roles for this member.");
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

                                    ElezenImgui.AttachTooltip(pair.Value != null && pair.Value.Value.IsPinned()
                                        ? "Unpin user"
                                        : "Pin user");
                                    ImGui.SameLine();

                                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                    {
                                        if (_uiSharedService.IconButton(FontAwesomeIcon.Trash))
                                        {
                                            _ = _apiController.GroupRemoveUser(new GroupPairDto(GroupFullInfo.Group, pair.Key.UserData));
                                        }
                                    }
                                    ElezenImgui.AttachTooltip("Remove user from Syncshell"
                                                                  + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");

                                    ImGui.SameLine();
                                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                    {
                                        if (_uiSharedService.IconButton(FontAwesomeIcon.Ban))
                                        {
                                            Mediator.Publish(new OpenBanUserPopupMessage(pair.Key, GroupFullInfo));
                                        }
                                    }
                                    ElezenImgui.AttachTooltip("Ban user from Syncshell"
                                                                  + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");
                                }
                            }
                        }
                    }
                }
                userNode.Dispose();
                var clearNode = ImRaii.TreeNode("Mass Cleanup");
                if (clearNode)
                {
                    using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                    {
                        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Broom, "Clear Syncshell"))
                        {
                            _ = _apiController.GroupClear(new(GroupFullInfo.Group));
                        }
                    }
                    ElezenImgui.AttachTooltip("This will remove all non-pinned, non-moderator users from the Syncshell."
                                                  + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");

                    ImGuiHelpers.ScaledDummy(2f);
                    ImGui.Separator();
                    ImGuiHelpers.ScaledDummy(2f);

                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Unlink, "Check for Inactive Users"))
                    {
                        _pruneTestTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: false);
                        _pruneTask = null;
                    }
                    ElezenImgui.AttachTooltip(string.Format("This will start the prune process for this Syncshell of inactive users that have not logged in the past {0} days.", _pruneDays)
                                                  + Environment.NewLine +"You will be able to review the amount of inactive users before executing the prune."
                                                  + UiSharedService.TooltipSeparator +"Note: pruning excludes pinned users and moderators of this Syncshell.");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(150);
                    _uiSharedService.DrawCombo("Days of inactivity", [7, 14, 30, 90], (count) =>
                    {
                        return string.Format("{0} days", count);
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
                            ElezenImgui.ColouredWrappedText("Calculating inactive users...", ImGuiColors.DalamudYellow);
                        }
                        else
                        {
                            ImGui.AlignTextToFramePadding();
                            ElezenImgui.WrappedText(string.Format("Found {0} user(s) that have not logged in the past {1} days.", _pruneTestTask.Result, _pruneDays));
                            if (_pruneTestTask.Result > 0)
                            {
                                using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                                {
                                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Broom, "Prune Inactive Users"))
                                    {
                                        _pruneTask = _apiController.GroupPrune(new(GroupFullInfo.Group), _pruneDays, execute: true);
                                        _pruneTestTask = null;
                                    }
                                }
                                ElezenImgui.AttachTooltip(string.Format("Pruning will remove {0} inactive user(s).", _pruneTestTask?.Result ?? 0)
                                                              + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");
                            }
                        }
                    }
                    if (_pruneTask != null)
                    {
                        if (!_pruneTask.IsCompleted)
                        {
                            ElezenImgui.ColouredWrappedText("Pruning Syncshell...", ImGuiColors.DalamudYellow);
                        }
                        else
                        {
                            ElezenImgui.WrappedText(string.Format("Syncshell was pruned and {0} inactive user(s) have been removed.", _pruneTask.Result));
                        }
                    }
                }
                clearNode.Dispose();

                var banNode = ImRaii.TreeNode("User Bans");
                if (banNode)
                {
                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Retweet, "Refresh Banlist from Server"))
                    {
                        _bannedUsers = _apiController.GroupGetBannedUsers(new GroupDto(GroupFullInfo.Group)).Result;
                    }

                    if (ImGui.BeginTable("bannedusertable" + GroupFullInfo.GID, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
                    {
                        ImGui.TableSetupColumn( "UID", ImGuiTableColumnFlags.None, 1);
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
                            ElezenImgui.WrappedText(bannedUser.Reason);
                            ImGui.TableNextColumn();
                            using var pushId = ImRaii.PushId(bannedUser.UID);
                            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Check, "Unban"))
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

            var permissionTab = ImRaii.TabItem("Permissions");
            if (permissionTab)
            {
                bool isDisableAnimations = perm.IsDisableAnimations();
                bool isDisableSounds = perm.IsDisableSounds();
                bool isDisableVfx = perm.IsDisableVFX();

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Sound Sync");
                ElezenImgui.GetBooleanIcon(!isDisableSounds);
                ImGui.SameLine(230);
                if (ElezenImgui.ShowIconButton(isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute,
                        isDisableSounds ? "Enable sound sync" : "Disable sound sync"))
                {
                    perm.SetDisableSounds(!perm.IsDisableSounds());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Animation Sync");
                ElezenImgui.GetBooleanIcon(!isDisableAnimations);
                ImGui.SameLine(230);
                if (ElezenImgui.ShowIconButton(isDisableAnimations ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop,
                        isDisableAnimations ? "Enable animation sync" : "Disable animation sync"))
                {
                    perm.SetDisableAnimations(!perm.IsDisableAnimations());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }

                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("VFX Sync");
                ElezenImgui.GetBooleanIcon(!isDisableVfx);
                ImGui.SameLine(230);
                if (ElezenImgui.ShowIconButton(isDisableVfx ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle,
                        isDisableVfx ? "Enable VFX sync" : "Disable VFX sync"))
                {
                    perm.SetDisableVFX(!perm.IsDisableVFX());
                    _ = _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, perm));
                }
            }
            permissionTab.Dispose();

            var auditTab = ImRaii.TabItem("Audit History");
            if (auditTab)
            {
                DrawAuditHistory();
            }
            auditTab.Dispose();

            if (_isOwner)
            {
                var ownerTab = ImRaii.TabItem("Owner Settings");
                if (ownerTab)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted("New Password");
                    var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
                    var buttonSize = ElezenImgui.GetIconButtonTextSize(FontAwesomeIcon.Passport, "Change Password");
                    var textSize = ImGui.CalcTextSize("New Password").X;
                    var spacing = ImGui.GetStyle().ItemSpacing.X;

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(availableWidth - buttonSize - textSize - spacing * 2);
                    ImGui.InputTextWithHint("##changepw", "Min 10 characters", ref _newPassword, 50);
                    ImGui.SameLine();
                    using (ImRaii.Disabled(_newPassword.Length < 10))
                    {
                        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Passport, "Change Password"))
                        {
                            _pwChangeSuccess = _apiController.GroupChangePassword(new GroupPasswordDto(GroupFullInfo.Group, _newPassword)).Result;
                            _newPassword = string.Empty;
                        }
                    }
                    ElezenImgui.AttachTooltip("Password requires to be at least 10 characters long. This action is irreversible.");
                    
                    if (!_pwChangeSuccess)
                    {
                        ElezenImgui.ColouredWrappedText("Failed to change the password. Password requires to be at least 10 characters long.", ImGuiColors.DalamudYellow);
                    }

                    if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete Syncshell") && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
                    {
                        IsOpen = false;
                        _ = _apiController.GroupDelete(new(GroupFullInfo.Group));
                    }
                    ElezenImgui.AttachTooltip("Hold CTRL and Shift and click to delete this Syncshell." + Environment.NewLine +"WARNING: this action is irreversible.");
                }
                ownerTab.Dispose();
            }
        }

        DrawMemberLabelEditorModal();
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    private void ConsumeAuditLogTask()
    {
        if (_auditLogTask == null || !_auditLogTask.IsCompleted)
        {
            return;
        }

        if (_auditLogTask.IsCompletedSuccessfully)
        {
            var result = _auditLogTask.Result;
            _auditEntries = result.Entries;
            _auditTotalCount = result.TotalCount;
        }
        else if (_auditLogTask.Exception != null)
        {
            _logger.LogWarning(_auditLogTask.Exception, "Failed to load syncshell audit history for {gid}", GroupFullInfo.GID);
        }

        _auditLogTask = null;
    }

    private void DrawAuditHistory()
    {
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Retweet, "Refresh Audit Log"))
        {
            RequestAuditLogPage(_auditSkip);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!_auditEntries.Any()))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Copy, "Copy Current Page"))
            {
                ImGui.SetClipboardText(BuildAuditClipboardText());
            }
        }

        ImGuiHelpers.ScaledDummy(2f);

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Action Filter", _auditActionFilter.HasValue ? FormatAuditAction(_auditActionFilter.Value) : "All Actions"))
        {
            if (ImGui.Selectable("All Actions", !_auditActionFilter.HasValue))
            {
                _auditActionFilter = null;
            }

            foreach (var action in Enum.GetValues<GroupAuditAction>())
            {
                var selected = _auditActionFilter == action;
                if (ImGui.Selectable(FormatAuditAction(action), selected))
                {
                    _auditActionFilter = action;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##auditSearch", "Filter actor/target/details", ref _auditSearch, 100);
        ImGui.SameLine();
        if (ImGui.Button("Apply Filters"))
        {
            RequestAuditLogPage(0);
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset"))
        {
            _auditActionFilter = null;
            _auditSearch = string.Empty;
            RequestAuditLogPage(0);
        }

        ImGuiHelpers.ScaledDummy(2f);

        using (ImRaii.Disabled(_auditSkip <= 0 || _auditLogTask != null))
        {
            if (ImGui.Button("Previous"))
            {
                RequestAuditLogPage(Math.Max(0, _auditSkip - AuditPageSize));
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(_auditSkip + AuditPageSize >= _auditTotalCount || _auditLogTask != null))
        {
            if (ImGui.Button("Next"))
            {
                RequestAuditLogPage(_auditSkip + AuditPageSize);
            }
        }

        ImGui.SameLine();
        var pageStart = _auditTotalCount == 0 ? 0 : _auditSkip + 1;
        var pageEnd = Math.Min(_auditSkip + _auditEntries.Count, _auditTotalCount);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"Showing {pageStart}-{pageEnd} of {_auditTotalCount}");

        if (_auditLogTask != null)
        {
            ElezenImgui.ColouredWrappedText("Loading audit history...", ImGuiColors.DalamudYellow);
        }

        using var table = ImRaii.Table("auditHistoryTable_" + GroupFullInfo.GID, 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY);
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Timestamp", ImGuiTableColumnFlags.WidthFixed, 170 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 140 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Actor", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        if (!_auditEntries.Any() && _auditLogTask == null)
        {
            ImGui.TableNextColumn();
            ElezenImgui.ColouredWrappedText("No audit entries found for the current filters.", ImGuiColors.DalamudYellow);
            return;
        }

        foreach (var entry in _auditEntries)
        {
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Timestamp.ToLocalTime().ToString(CultureInfo.CurrentCulture));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatAuditAction(entry.Action));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.ActorUID);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.TargetUID ?? "-");

            ImGui.TableNextColumn();
            ElezenImgui.WrappedText(entry.Details ?? string.Empty);
        }
    }

    private string BuildAuditClipboardText()
    {
        StringBuilder builder = new();
        foreach (var entry in _auditEntries)
        {
            builder.Append(entry.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            builder.Append('\t');
            builder.Append(entry.GroupGID);
            builder.Append('\t');
            builder.Append(FormatAuditAction(entry.Action));
            builder.Append('\t');
            builder.Append(entry.ActorUID);
            builder.Append('\t');
            builder.Append(entry.TargetUID ?? string.Empty);
            builder.Append('\t');
            builder.Append(entry.Details ?? string.Empty);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatAuditAction(GroupAuditAction action)
    {
        return action switch
        {
            GroupAuditAction.Create => "Create",
            GroupAuditAction.Join => "Join",
            GroupAuditAction.Leave => "Leave",
            GroupAuditAction.Kick => "Kick",
            GroupAuditAction.Ban => "Ban",
            GroupAuditAction.Unban => "Unban",
            GroupAuditAction.Mod => "Mod",
            GroupAuditAction.Unmod => "Unmod",
            GroupAuditAction.Pin => "Pin",
            GroupAuditAction.Unpin => "Unpin",
            GroupAuditAction.OwnershipTransfer => "Ownership Transfer",
            GroupAuditAction.PermissionChange => "Permission Change",
            GroupAuditAction.PasswordChange => "Password Change",
            GroupAuditAction.TempInviteCreate => "Temp Invite Create",
            GroupAuditAction.GroupClear => "Group Clear",
            GroupAuditAction.VenueRegistrationChange => "Venue Change",
            GroupAuditAction.MemberLabelAdd => "Role Add",
            GroupAuditAction.MemberLabelRemove => "Role Remove",
            _ => action.ToString(),
        };
    }

    private void RequestAuditLogPage(int skip)
    {
        _auditSkip = Math.Max(0, skip);
        _auditLogTask = _apiController.GroupGetAuditLog(new GroupAuditQueryDto(GroupFullInfo.Group, _auditSkip, AuditPageSize)
        {
            Action = _auditActionFilter,
            Search = string.IsNullOrWhiteSpace(_auditSearch) ? null : _auditSearch.Trim()
        });
    }

    private void OpenMemberLabelEditor(GroupPairFullInfoDto memberInfo)
    {
        _memberLabelEditorTarget = memberInfo;
        _memberLabelDraft = SyncshellMemberLabelUi.NormalizeSingleSelection(memberInfo.MemberLabels);
        _memberLabelError = string.Empty;
        _showMemberLabelEditor = true;
        _memberLabelEditorPopupPendingOpen = true;
    }

    private void DrawMemberLabelEditorModal()
    {
        var popupTitle = "Edit Syncshell Roles";
        if (_memberLabelEditorPopupPendingOpen)
        {
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
            ImGui.OpenPopup(popupTitle);
            _memberLabelEditorPopupPendingOpen = false;
        }

        if (ImGui.BeginPopupModal(popupTitle, ref _showMemberLabelEditor, UiSharedService.PopupWindowFlags))
        {
            if (_memberLabelEditorTarget == null)
            {
                _showMemberLabelEditor = false;
                ImGui.EndPopup();
                return;
            }

            ElezenImgui.WrappedText($"Select shared roles for {_memberLabelEditorTarget.UserAliasOrUID} in {GroupFullInfo.GroupAliasOrGID}.");
            ElezenImgui.ColouredWrappedText(
                "Choose a role for this user in your syncshell. They'll be given a special icon to help people identify their job.",
                ImGuiColors.DalamudGrey);
            ImGui.Separator();

            foreach (var role in GroupMemberLabelValidator.AvailableLabels)
            {
                using var roleId = ImRaii.PushId($"admin-member-role-{role.Value}");
                var isSelected = SyncshellMemberLabelUi.IsLabelSelected(_memberLabelDraft, role.Value);
                if (ImGui.Checkbox("##selected", ref isSelected))
                {
                    if (SyncshellMemberLabelUi.TrySetExclusiveLabelSelected(role.Value, isSelected, out var updatedLabels, out var errorMessage))
                    {
                        _memberLabelDraft = updatedLabels;
                        _memberLabelError = string.Empty;
                    }
                    else
                    {
                        _memberLabelError = errorMessage ?? "Unable to update the selected roles.";
                    }
                }
                ImGui.SameLine();
                if (SyncshellMemberLabelUi.TryGetLabelPresentation(role.Value, out var icon, out var color, out var displayName, out _))
                {
                    using var roleColor = ImRaii.PushColor(ImGuiCol.Text, color);
                    ImGui.PushFont(UiBuilder.IconFont);
                    ImGui.TextUnformatted(icon.ToIconString());
                    ImGui.PopFont();
                    ImGui.SameLine();
                    ImGui.TextUnformatted(displayName);
                }
                else
                {
                    ImGui.TextUnformatted(role.DisplayName);
                }
            }

            if (!string.IsNullOrEmpty(_memberLabelError))
            {
                ElezenImgui.ColouredWrappedText(_memberLabelError, ImGuiColors.DalamudRed);
            }

            ImGuiHelpers.ScaledDummy(2f);
            if (_memberLabelDraft.Count == 0)
            {
                ElezenImgui.ColouredWrappedText("No role selected.", ImGuiColors.DalamudGrey);
            }
            else
            {
                ElezenImgui.WrappedText("Selected Role: " + SyncshellMemberLabelUi.FormatLabels(_memberLabelDraft));
            }

            ImGui.Separator();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save Role"))
            {
                var success = _apiController.GroupSetMemberLabels(new GroupMemberLabelsDto(GroupFullInfo.Group, _memberLabelEditorTarget.User, _memberLabelDraft)).Result;
                if (success)
                {
                    _showMemberLabelEditor = false;
                }
                else
                {
                    _memberLabelError = "Unable to save roles. The member may have left the syncshell, your permissions changed, or the selection failed validation.";
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _showMemberLabelEditor = false;
            }

            UiSharedService.SetScaledWindowSize(430, centerWindow: false);
            ImGui.EndPopup();
        }
    }
}
