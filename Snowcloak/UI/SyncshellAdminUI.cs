using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
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

public partial class SyncshellAdminUI : WindowMediatorSubscriberBase
{
    private const int AuditPageSize = 50;
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _configService;
    private readonly bool _isModerator;
    private readonly bool _isOwner;
    private readonly AsyncOp<GroupAliasResponseDto> _aliasChangeOperation = new();
    private readonly AsyncOp<GroupAuditPageDto> _auditLogOperation = new();
    private readonly AsyncOp<List<string>> _bulkInviteOperation = new();
    private readonly AsyncOp _groupPermissionOperation = new();
    private readonly AsyncOp<bool> _memberLabelSaveOperation = new();
    private readonly List<string> _oneTimeInvites = [];
    private readonly PairManager _pairManager;
    private readonly AsyncOp<bool> _passwordChangeOperation = new();
    private readonly AsyncOp<List<string>> _singleInviteOperation = new();
    private readonly SyncshellCommunityManagementPanel _communityManagementPanel;
    private readonly SyncshellMemberManagementPanel _memberManagementPanel;
    private readonly SyncshellBudgetPanel _syncshellBudgetPanel;
    private readonly UiFontService _fontService;
    private GroupAuditAction? _auditActionFilter;
    private List<GroupAuditEntryDto> _auditEntries = [];
    private string _auditSearch = string.Empty;
    private int _auditSkip;
    private int _auditTotalCount;
    private int _multiInvites;
    private string _aliasChangeMessage = string.Empty;
    private bool _aliasChangeIsError;
    private string _newPassword;
    private string _syncshellAlias;
    private GroupPairFullInfoDto? _memberLabelEditorTarget;
    private List<string> _memberLabelDraft = [];
    private string _memberLabelError = string.Empty;
    private bool _memberLabelEditorPopupPendingOpen;
    private string _passwordChangeMessage = string.Empty;
    private bool _passwordChangeIsError;
    private bool _showMemberLabelEditor;
    private SyncshellAdminTab _selectedTab = SyncshellAdminTab.Settings;

    private enum SyncshellAdminTab
    {
        Performance,
        Settings,
        Community,
        Directory,
        Invites,
        Members,
        Cleanup,
        Bans,
        Permissions,
        Audit,
        Owner,
    }

    public SyncshellAdminUI(ILogger<SyncshellAdminUI> logger, SnowMediator mediator, ApiController apiController,
        SnowcloakConfigService configService,
        UiFontService fontService, PairManager pairManager, GroupFullInfoDto groupFullInfo, PerformanceCollectorService performanceCollectorService,
        SyncshellBudgetService syncshellBudgetService, DalamudUtilService dalamudUtilService)
        : base(logger, mediator, BuildWindowTitle(groupFullInfo), performanceCollectorService)
    {
        ArgumentNullException.ThrowIfNull(groupFullInfo);
        GroupFullInfo = groupFullInfo;
        _apiController = apiController;
        _configService = configService;
        _fontService = fontService;
        _pairManager = pairManager;
        _syncshellBudgetPanel = new(syncshellBudgetService);
        _communityManagementPanel = new(apiController, dalamudUtilService);
        _memberManagementPanel = new(apiController, mediator, pairManager);
        _isOwner = string.Equals(GroupFullInfo.OwnerUID, _apiController.UID, StringComparison.Ordinal);
        _isModerator = GroupFullInfo.GroupUserInfo.IsModerator();
        _newPassword = string.Empty;
        _syncshellAlias = groupFullInfo.Group.Alias ?? string.Empty;
        _multiInvites = 30;
        IsOpen = true;
        RequestAuditLogPage(0);
        SetScaledSizeConstraints(new Vector2(700, 500), new Vector2(700, 2000));
    }

    public GroupFullInfoDto GroupFullInfo { get; private set; }

    private static string BuildWindowTitle(GroupFullInfoDto groupFullInfo)
    {
        ArgumentNullException.ThrowIfNull(groupFullInfo);
        return string.Format(CultureInfo.CurrentCulture, "Syncshell Admin Panel ({0})", groupFullInfo.GroupAliasOrGID);
    }
    
    protected override void DrawInternal()
    {
        if (!_isModerator && !_isOwner) return;
        ConsumeAuditLogTask();
        ConsumeAliasChangeTask();
        ConsumeInviteTasks();
        ConsumePasswordChangeTask();

        GroupFullInfo = _pairManager.Groups[GroupFullInfo.Group];

        using var id = ImRaii.PushId("syncshell_admin_" + GroupFullInfo.GID);

        using (_fontService.UidFont.Push())
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "{0} Administrative Panel", GroupFullInfo.GroupAliasOrGID));
        ImGui.Separator();

        NormalizeSelectedTab();
        DrawAdminSidebar();
        ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
        DrawAdminContent();

        DrawMemberLabelEditorModal();
    }

    private void DrawAdminContent()
    {
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactBg);
        using var contentPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16f, 12f) * ImGuiHelpers.GlobalScale);
        using var content = ImRaii.Child("syncshell_admin_content", new Vector2(-1, -1), false);
        if (!content)
        {
            return;
        }

        switch (_selectedTab)
        {
            case SyncshellAdminTab.Performance:
                DrawPerformanceTab();
                break;
            case SyncshellAdminTab.Settings:
                DrawSyncshellSettings();
                break;
            case SyncshellAdminTab.Community:
                _communityManagementPanel.DrawCommunity(GroupFullInfo);
                break;
            case SyncshellAdminTab.Directory:
                _communityManagementPanel.DrawDirectory(GroupFullInfo);
                break;
            case SyncshellAdminTab.Invites:
                DrawInvitesTab();
                break;
            case SyncshellAdminTab.Members:
                _memberManagementPanel.DrawMembers(GroupFullInfo, _isOwner, _isModerator, OpenMemberLabelEditor);
                break;
            case SyncshellAdminTab.Cleanup:
                _memberManagementPanel.DrawCleanup(GroupFullInfo);
                break;
            case SyncshellAdminTab.Bans:
                _memberManagementPanel.DrawBans(GroupFullInfo);
                break;
            case SyncshellAdminTab.Permissions:
                DrawPermissionsTab();
                break;
            case SyncshellAdminTab.Audit:
                DrawAuditHistory();
                break;
            case SyncshellAdminTab.Owner:
                DrawOwnerSettingsTab();
                break;
        }
    }

    private void DrawPerformanceTab()
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

    private void DrawInvitesTab()
    {
        var perm = GroupFullInfo.GroupPermissions;
        bool isInvitesDisabled = perm.IsDisableInvites();

        using (ImRaii.Disabled(_groupPermissionOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(isInvitesDisabled ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock,
                    isInvitesDisabled ? "Unlock Syncshell" : "Lock Syncshell"))
            {
                perm.SetDisableInvites(!isInvitesDisabled);
                RunGroupPermissionChange(perm);
            }
        }
        DrawOperationStatus(_groupPermissionOperation, "Saving...");

        ImGuiHelpers.ScaledDummy(2f);

        ElezenImgui.WrappedText("One-time invites work as single-use passwords. Use those if you do not want to distribute your Syncshell password.");
        using (ImRaii.Disabled(_singleInviteOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Envelope, "Single one-time invite"))
            {
                _ = _singleInviteOperation.Run(() => _apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), 1));
            }
        }
        ElezenImgui.AttachTooltip("Creates a single-use password for joining the syncshell which is valid for 24h and copies it to the clipboard.");
        DrawOperationStatus(_singleInviteOperation, "Generating...");
        ImGui.InputInt("##amountofinvites", ref _multiInvites);
        ImGui.SameLine();
        using (ImRaii.Disabled(_multiInvites <= 1 || _multiInvites > 100 || _bulkInviteOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Envelope, string.Format(CultureInfo.CurrentCulture, "Generate {0} one-time invites", _multiInvites)))
            {
                _ = _bulkInviteOperation.Run(() => _apiController.GroupCreateTempInvite(new(GroupFullInfo.Group), _multiInvites));
            }
        }
        DrawOperationStatus(_bulkInviteOperation, "Generating...");

        if (_oneTimeInvites.Count > 0)
        {
            var invites = string.Join(Environment.NewLine, _oneTimeInvites);
            ImGui.InputTextMultiline("Generated Multi Invites", ref invites, 5000, new(0, 0), ImGuiInputTextFlags.ReadOnly);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Copy, "Copy Invites to clipboard"))
            {
                ImGui.SetClipboardText(invites);
            }
        }
    }

    private void DrawPermissionsTab()
    {
        var perm = GroupFullInfo.GroupPermissions;
        bool isDisableAnimations = perm.IsDisableAnimations();
        bool isDisableSounds = perm.IsDisableSounds();
        bool isDisableVfx = perm.IsDisableVFX();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Sound Sync");
        ElezenImgui.GetBooleanIcon(!isDisableSounds);
        ImGui.SameLine(230);
        using (ImRaii.Disabled(_groupPermissionOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute,
                    isDisableSounds ? "Enable sound sync" : "Disable sound sync"))
            {
                perm.SetDisableSounds(!perm.IsDisableSounds());
                RunGroupPermissionChange(perm);
            }
        }
        DrawOperationStatus(_groupPermissionOperation, "Saving...");

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Animation Sync");
        ElezenImgui.GetBooleanIcon(!isDisableAnimations);
        ImGui.SameLine(230);
        using (ImRaii.Disabled(_groupPermissionOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(isDisableAnimations ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop,
                    isDisableAnimations ? "Enable animation sync" : "Disable animation sync"))
            {
                perm.SetDisableAnimations(!perm.IsDisableAnimations());
                RunGroupPermissionChange(perm);
            }
        }
        DrawOperationStatus(_groupPermissionOperation, "Saving...");

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("VFX Sync");
        ElezenImgui.GetBooleanIcon(!isDisableVfx);
        ImGui.SameLine(230);
        using (ImRaii.Disabled(_groupPermissionOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(isDisableVfx ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle,
                    isDisableVfx ? "Enable VFX sync" : "Disable VFX sync"))
            {
                perm.SetDisableVFX(!perm.IsDisableVFX());
                RunGroupPermissionChange(perm);
            }
        }
        DrawOperationStatus(_groupPermissionOperation, "Saving...");
    }

    private void DrawOwnerSettingsTab()
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
        using (ImRaii.Disabled(_newPassword.Length < 10 || _passwordChangeOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Passport, "Change Password"))
            {
                var newPassword = _newPassword;
                _passwordChangeMessage = string.Empty;
                _passwordChangeIsError = false;
                _newPassword = string.Empty;
                _ = _passwordChangeOperation.Run(() => _apiController.GroupChangePassword(new GroupPasswordDto(GroupFullInfo.Group, newPassword)));
            }
        }
        ElezenImgui.AttachTooltip("Password requires to be at least 10 characters long. This action is irreversible.");

        DrawOperationStatus(_passwordChangeOperation, "Changing password...");
        if (!string.IsNullOrWhiteSpace(_passwordChangeMessage))
        {
            ElezenImgui.ColouredWrappedText(_passwordChangeMessage,
                _passwordChangeIsError ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen);
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete Syncshell") && ElezenImgui.CtrlPressed() && ElezenImgui.ShiftPressed())
        {
            IsOpen = false;
            _ = _apiController.GroupDelete(new(GroupFullInfo.Group));
        }
        ElezenImgui.AttachTooltip("Hold CTRL and Shift and click to delete this Syncshell." + Environment.NewLine + "WARNING: this action is irreversible.");
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    private void DrawSyncshellSettings()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Syncshell ID");
        ImGui.SameLine();
        ImGui.TextDisabled(GroupFullInfo.GID);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Syncshell name");
        var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        var buttonSize = ElezenImgui.GetIconButtonTextSize(FontAwesomeIcon.Save, "Save Name");
        var textSize = ImGui.CalcTextSize("Syncshell name").X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var requestedAlias = _syncshellAlias.Trim();
        var currentAlias = GroupFullInfo.Group.Alias ?? string.Empty;
        var aliasChanged = !string.Equals(requestedAlias, currentAlias, StringComparison.Ordinal);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(availableWidth - buttonSize - textSize - spacing * 2);
        ImGui.InputTextWithHint("##syncshellalias", "Blank uses Syncshell ID", ref _syncshellAlias, 50);
        ImGui.SameLine();
        using (ImRaii.Disabled(!aliasChanged || _aliasChangeOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save Name"))
            {
                _aliasChangeMessage = string.Empty;
                _aliasChangeIsError = false;
                _ = _aliasChangeOperation.Run(() => _apiController.GroupChangeAlias(new GroupAliasDto(
                    GroupFullInfo.Group,
                    string.IsNullOrWhiteSpace(requestedAlias) ? null : requestedAlias)));
            }
        }
        ElezenImgui.AttachTooltip("Syncshell names must be unique. Leave blank to show the Syncshell ID.");
        DrawOperationStatus(_aliasChangeOperation, "Saving...");

        if (!string.IsNullOrWhiteSpace(_aliasChangeMessage))
        {
            ElezenImgui.ColouredWrappedText(_aliasChangeMessage,
                _aliasChangeIsError ? ImGuiColors.DalamudYellow : ImGuiColors.HealerGreen);
        }
    }

    private void ConsumeAliasChangeTask()
    {
        if (!_aliasChangeOperation.IsCompleted)
        {
            return;
        }

        if (!_aliasChangeOperation.Faulted)
        {
            var result = _aliasChangeOperation.Result;
            if (result?.Success == true)
            {
                _syncshellAlias = result.GroupInfo?.Group.Alias ?? string.Empty;
                _aliasChangeMessage = "Syncshell name updated.";
                _aliasChangeIsError = false;
            }
            else
            {
                _aliasChangeMessage = string.IsNullOrWhiteSpace(result?.ErrorMessage)
                    ? "Failed to update syncshell name."
                    : result.ErrorMessage;
                _aliasChangeIsError = true;
            }
        }
        else
        {
            LogSyncshellAliasUpdateFailed(_logger, GroupFullInfo.GID, _aliasChangeOperation.Error);
            _aliasChangeMessage = "Failed to update syncshell name.";
            _aliasChangeIsError = true;
        }

        _aliasChangeOperation.Reset();
    }

    private void ConsumeInviteTasks()
    {
        if (_singleInviteOperation.IsCompleted)
        {
            if (!_singleInviteOperation.Faulted)
            {
                ImGui.SetClipboardText(_singleInviteOperation.Result?.FirstOrDefault() ?? string.Empty);
            }

            _singleInviteOperation.Reset();
        }

        if (_bulkInviteOperation.IsCompleted)
        {
            if (!_bulkInviteOperation.Faulted)
            {
                _oneTimeInvites.AddRange(_bulkInviteOperation.Result ?? []);
            }

            _bulkInviteOperation.Reset();
        }
    }

    private void ConsumePasswordChangeTask()
    {
        if (!_passwordChangeOperation.IsCompleted)
        {
            return;
        }

        if (_passwordChangeOperation.Faulted)
        {
            _passwordChangeMessage = _passwordChangeOperation.Error ?? "Failed to change the password.";
            _passwordChangeIsError = true;
        }
        else if (_passwordChangeOperation.Result)
        {
            _passwordChangeMessage = "Syncshell password updated.";
            _passwordChangeIsError = false;
        }
        else
        {
            _passwordChangeMessage = "Failed to change the password. Password requires to be at least 10 characters long.";
            _passwordChangeIsError = true;
        }

        _passwordChangeOperation.Reset();
    }

    private void ConsumeAuditLogTask()
    {
        if (!_auditLogOperation.IsCompleted)
        {
            return;
        }

        if (!_auditLogOperation.Faulted)
        {
            var result = _auditLogOperation.Result;
            _auditEntries = result?.Entries ?? [];
            _auditTotalCount = result?.TotalCount ?? 0;
        }
        else
        {
            LogSyncshellAuditLoadFailed(_logger, GroupFullInfo.GID, _auditLogOperation.Error);
        }

        _auditLogOperation.Reset();
    }

    private void RunGroupPermissionChange(GroupPermissions permissions)
    {
        _ = _groupPermissionOperation.Run(() => _apiController.GroupChangeGroupPermissionState(new(GroupFullInfo.Group, permissions)));
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

    private void DrawAuditHistory()
    {
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Retweet, "Refresh Audit Log"))
        {
            RequestAuditLogPage(_auditSkip);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(_auditEntries.Count == 0))
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

        using (ImRaii.Disabled(_auditSkip <= 0 || _auditLogOperation.IsRunning))
        {
            if (ImGui.Button("Previous"))
            {
                RequestAuditLogPage(Math.Max(0, _auditSkip - AuditPageSize));
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(_auditSkip + AuditPageSize >= _auditTotalCount || _auditLogOperation.IsRunning))
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

        if (_auditLogOperation.IsRunning)
        {
            ElezenImgui.ColouredWrappedText("Loading audit history...", ImGuiColors.DalamudYellow);
        }
        else if (_auditLogOperation.Faulted)
        {
            ElezenImgui.ColouredWrappedText(_auditLogOperation.Error ?? "Failed to load audit history.", ImGuiColors.DalamudRed);
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

        if (_auditEntries.Count == 0 && !_auditLogOperation.IsRunning)
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
            GroupAuditAction.AliasChange => "Name Change",
            _ => action.ToString(),
        };
    }

    private void RequestAuditLogPage(int skip)
    {
        _auditSkip = Math.Max(0, skip);
        _ = _auditLogOperation.Run(() => _apiController.GroupGetAuditLog(new GroupAuditQueryDto(GroupFullInfo.Group, _auditSkip, AuditPageSize)
        {
            Action = _auditActionFilter,
            Search = string.IsNullOrWhiteSpace(_auditSearch) ? null : _auditSearch.Trim()
        }));
    }

    private void OpenMemberLabelEditor(GroupPairFullInfoDto memberInfo)
    {
        _memberLabelEditorTarget = memberInfo;
        _memberLabelDraft = SyncshellMemberLabelUi.NormalizeSingleSelection(memberInfo.MemberLabels);
        _memberLabelError = string.Empty;
        _memberLabelSaveOperation.Reset();
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

        if (ImGui.BeginPopupModal(popupTitle, ref _showMemberLabelEditor, SnowcloakUi.PopupWindowFlags))
        {
            ConsumeMemberLabelSave();

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
            using (ImRaii.Disabled(_memberLabelSaveOperation.IsRunning))
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save Role"))
                {
                    var target = _memberLabelEditorTarget;
                    var labels = _memberLabelDraft.ToList();
                    _ = _memberLabelSaveOperation.Run(() => _apiController.GroupSetMemberLabels(new GroupMemberLabelsDto(GroupFullInfo.Group, target.User, labels)));
                }
            }
            DrawOperationStatus(_memberLabelSaveOperation, "Saving...");

            ImGui.SameLine();
            using (ImRaii.Disabled(_memberLabelSaveOperation.IsRunning))
            {
                if (ImGui.Button("Cancel"))
                {
                    _showMemberLabelEditor = false;
                }
            }

            ElezenImgui.SetScaledWindowSize(430, centerWindow: false);
            ImGui.EndPopup();
        }
    }

    private void ConsumeMemberLabelSave()
    {
        if (!_memberLabelSaveOperation.IsCompleted)
        {
            return;
        }

        if (_memberLabelSaveOperation.Faulted)
        {
            _memberLabelError = _memberLabelSaveOperation.Error ?? "Unable to save roles.";
        }
        else if (_memberLabelSaveOperation.Result)
        {
            _showMemberLabelEditor = false;
        }
        else
        {
            _memberLabelError = "Unable to save roles. The member may have left the syncshell, your permissions changed, or the selection failed validation.";
        }

        _memberLabelSaveOperation.Reset();
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to update syncshell alias for {Gid}: {Message}")]
    private static partial void LogSyncshellAliasUpdateFailed(ILogger logger, string gid, string? message);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to load syncshell audit history for {Gid}: {Message}")]
    private static partial void LogSyncshellAuditLoadFailed(ILogger logger, string gid, string? message);
}
