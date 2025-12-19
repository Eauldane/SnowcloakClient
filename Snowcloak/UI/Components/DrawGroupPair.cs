using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Handlers;
using Snowcloak.WebAPI;
using Snowcloak.Services.Localisation;

namespace Snowcloak.UI.Components;

public class DrawGroupPair : DrawPairBase
{
    protected readonly SnowMediator _mediator;
    private readonly GroupPairFullInfoDto _fullInfoDto;
    private readonly GroupFullInfoDto _group;
    private readonly CharaDataManager _charaDataManager;
    public long VRAMUsage { get; set; }
    private readonly LocalisationService _localisationService;

    public DrawGroupPair(string id, Pair entry, ApiController apiController,
        SnowMediator snowMediator, GroupFullInfoDto group, GroupPairFullInfoDto fullInfoDto,
        UidDisplayHandler handler, UiSharedService uiSharedService, CharaDataManager charaDataManager, LocalisationService localisationService)
        : base(id, entry, apiController, handler, uiSharedService)
    {
        _group = group;
        _fullInfoDto = fullInfoDto;
        _mediator = snowMediator;
        _charaDataManager = charaDataManager;
        _localisationService = localisationService;
    }

    protected override void DrawLeftSide(float textPosY, float originalY)
    {
        var entryUID = _pair.UserData.AliasOrUID;
        var entryIsMod = _fullInfoDto.GroupPairStatusInfo.IsModerator();
        var entryIsOwner = string.Equals(_pair.UserData.UID, _group.OwnerUID, StringComparison.Ordinal);
        var entryIsPinned = _fullInfoDto.GroupPairStatusInfo.IsPinned();
        var presenceIcon = _pair.IsVisible ? FontAwesomeIcon.Eye : (_pair.IsOnline ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink);
        var presenceColor = (_pair.IsOnline || _pair.IsVisible) ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed;
        var presenceText = LF("Offline", "{0} is offline", entryUID);
        
        ImGui.SetCursorPosY(textPosY);
        if (_pair.IsPaused)
        {
            presenceIcon = FontAwesomeIcon.Question;
            presenceColor = ImGuiColors.DalamudGrey;
            presenceText = LF("StatusUnknown", "{0} online status is unknown (paused)", entryUID);
            
            ImGui.PushFont(UiBuilder.IconFont);
            UiSharedService.ColorText(FontAwesomeIcon.PauseCircle.ToIconString(), ImGuiColors.DalamudYellow);
            ImGui.PopFont();

            UiSharedService.AttachToolTip(LF("PairingPaused", "Pairing status with {0} is paused", entryUID));
        }
        else
        {
            ImGui.PushFont(UiBuilder.IconFont);
            UiSharedService.ColorText(FontAwesomeIcon.Check.ToIconString(), ImGuiColors.ParsedGreen);
            ImGui.PopFont();

            UiSharedService.AttachToolTip(LF("PairedWith", "You are paired with {0}", entryUID));
        }

        if (_pair.IsOnline && !_pair.IsVisible) presenceText = LF("Online", "{0} is online", entryUID);
        else if (_pair.IsOnline && _pair.IsVisible) presenceText = LF("Visible", "{0} is visible: {1}{2}Click to target this player", entryUID, _pair.PlayerName, Environment.NewLine);

        ImGui.SameLine();
        ImGui.SetCursorPosY(textPosY);
        ImGui.PushFont(UiBuilder.IconFont);
        UiSharedService.ColorText(presenceIcon.ToIconString(), presenceColor);
        ImGui.PopFont();
        if (_pair.IsVisible)
        {
            if (ImGui.IsItemClicked())
            {
                _mediator.Publish(new TargetPairMessage(_pair));
            }
            if (_pair.LastAppliedDataBytes >= 0)
            {

                presenceText += UiSharedService.TooltipSeparator;
                presenceText += ((!_pair.IsVisible) ? L("LastPrefix", "(Last) ") : string.Empty) + L("ModsInfo", "Mods Info") + Environment.NewLine;
                presenceText += L("FilesSize", "Files Size: ") + UiSharedService.ByteToString(_pair.LastAppliedDataBytes, true);
                if (_pair.LastAppliedApproximateVRAMBytes >= 0)
                {
                    presenceText += Environment.NewLine + L("ApproxVram", "Approx. VRAM Usage: ") + UiSharedService.ByteToString(_pair.LastAppliedApproximateVRAMBytes, true);
                }
                if (_pair.LastAppliedDataTris >= 0)
                {
                    presenceText += Environment.NewLine + L("TriangleCount", "Triangle Count (excl. Vanilla): ")
                                                        + (_pair.LastAppliedDataTris > 1000 ? (_pair.LastAppliedDataTris / 1000d).ToString("0.0'k'") : _pair.LastAppliedDataTris);
                }
            }
        }
        UiSharedService.AttachToolTip(presenceText);

        if (entryIsOwner)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Crown.ToIconString());
            ImGui.PopFont();
            UiSharedService.AttachToolTip(L("OwnerTooltip", "User is owner of this Syncshell"));
        }
        else if (entryIsMod)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.UserShield.ToIconString());
            ImGui.PopFont();
            UiSharedService.AttachToolTip(L("ModeratorTooltip", "User is moderator of this Syncshell"));
        }
        else if (entryIsPinned)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Thumbtack.ToIconString());
            ImGui.PopFont();
            UiSharedService.AttachToolTip(L("PinnedTooltip", "User is pinned in this Syncshell"));
        }
    }

    protected override float DrawRightSide(float textPosY, float originalY)
    {
        var pauseIcon = _pair.IsPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var pauseIconSize = _uiSharedService.GetIconButtonSize(pauseIcon);
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var entryUID = _fullInfoDto.UserAliasOrUID;
        var entryIsMod = _fullInfoDto.GroupPairStatusInfo.IsModerator();
        var entryIsOwner = string.Equals(_pair.UserData.UID, _group.OwnerUID, StringComparison.Ordinal);
        var entryIsPinned = _fullInfoDto.GroupPairStatusInfo.IsPinned();
        var userIsOwner = string.Equals(_group.OwnerUID, _apiController.UID, StringComparison.OrdinalIgnoreCase);
        var userIsModerator = _group.GroupUserInfo.IsModerator();

        var soundsDisabled = _fullInfoDto.GroupUserPermissions.IsDisableSounds();
        var animDisabled = _fullInfoDto.GroupUserPermissions.IsDisableAnimations();
        var vfxDisabled = _fullInfoDto.GroupUserPermissions.IsDisableVFX();
        var individualSoundsDisabled = (_pair.UserPair?.OwnPermissions.IsDisableSounds() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableSounds() ?? false);
        var individualAnimDisabled = (_pair.UserPair?.OwnPermissions.IsDisableAnimations() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableAnimations() ?? false);
        var individualVFXDisabled = (_pair.UserPair?.OwnPermissions.IsDisableVFX() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableVFX() ?? false);

        bool showShared = _charaDataManager.SharedWithYouData.TryGetValue(_pair.UserData, out var sharedData);
        bool showInfo = (individualAnimDisabled || individualSoundsDisabled || animDisabled || soundsDisabled);
        bool showPlus = _pair.UserPair == null;
        bool showBars = (userIsOwner || (userIsModerator && !entryIsMod && !entryIsOwner)) || !_pair.IsPaused;
        bool showPause = true; 
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var permIcon = (individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled) ? FontAwesomeIcon.ExclamationTriangle
            : ((soundsDisabled || animDisabled || vfxDisabled) ? FontAwesomeIcon.InfoCircle : FontAwesomeIcon.None);
        var runningIconWidth = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Running).X;
        var infoIconWidth = UiSharedService.GetIconSize(permIcon).X;
        var plusButtonWidth = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus).X;
        var pauseButtonWidth = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus).X;

        var barButtonWidth = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Bars).X;
        var barButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Bars);

        var windowEndX = ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth();
        var rightSidePos = windowEndX - barButtonSize.X;


        var pos = ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() + spacing
            - (showShared ? (runningIconWidth + spacing) : 0)
            - (showInfo ? (infoIconWidth + spacing) : 0)
            - (showPlus ? (plusButtonWidth + spacing) : 0)
            - (showPause ? (pauseButtonWidth + spacing) : 0)
            - (showBars ? (barButtonWidth + spacing) : 0);

        ImGui.SameLine(pos);

        if (showShared)
        {
            _uiSharedService.IconText(FontAwesomeIcon.Running);

            UiSharedService.AttachToolTip(LF("SharedData", "This user has shared {0} Character Data Sets with you.", sharedData!.Count) + UiSharedService.TooltipSeparator
                + L("OpenCharaHub", "Click to open the Character Data Hub and show the entries."));

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _mediator.Publish(new OpenCharaDataHubWithFilterMessage(_pair.UserData));
            }
            ImGui.SameLine();
        }

        if (individualAnimDisabled || individualSoundsDisabled)
        {
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            _uiSharedService.IconText(permIcon);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();

                ImGui.TextUnformatted(L("IndividualPermissions", "Individual User permissions"));
                
                if (individualSoundsDisabled)
                {
                    var userSoundsText = LF("SoundSyncDisabled", "Sound sync disabled with {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.VolumeOff);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userSoundsText);
                    ImGui.NewLine();
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(LF("SoundStatus", "You: {0}, They: {1}", _pair.UserPair!.OwnPermissions.IsDisableSounds() ? L("Disabled", "Disabled") : L("Enabled", "Enabled"), _pair.UserPair!.OtherPermissions.IsDisableSounds() ? L("Disabled", "Disabled") : L("Enabled", "Enabled")));
                }

                if (individualAnimDisabled)
                {
                    var userAnimText = LF("AnimationSyncDisabled", "Animation sync disabled with {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.Stop);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userAnimText);
                    ImGui.NewLine();
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(LF("AnimationStatus", "You: {0}, They: {1}", _pair.UserPair!.OwnPermissions.IsDisableAnimations() ? L("Disabled", "Disabled") : L("Enabled", "Enabled"), _pair.UserPair!.OtherPermissions.IsDisableAnimations() ? L("Disabled", "Disabled") : L("Enabled", "Enabled")));
                }

                if (individualVFXDisabled)
                {
                    var userVFXText = LF("VfxSyncDisabled", "VFX sync disabled with {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.Circle);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userVFXText);
                    ImGui.NewLine();
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(LF("VfxStatus", "You: {0}, They: {1}", _pair.UserPair!.OwnPermissions.IsDisableVFX() ? L("Disabled", "Disabled") : L("Enabled", "Enabled"), _pair.UserPair!.OtherPermissions.IsDisableVFX() ? L("Disabled", "Disabled") : L("Enabled", "Enabled")));
                }

                ImGui.EndTooltip();
            }
            ImGui.SameLine();
        }
        else if ((animDisabled || soundsDisabled))
        {
            ImGui.SetCursorPosY(textPosY);
            _uiSharedService.IconText(permIcon);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();

                ImGui.TextUnformatted(L("SyncshellPermissions", "Syncshell User permissions"));
                
                if (soundsDisabled)
                {
                    var userSoundsText = LF("SoundDisabledBy", "Sound sync disabled by {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.VolumeOff);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userSoundsText);
                }

                if (animDisabled)
                {
                    var userAnimText = LF("AnimationDisabledBy", "Animation sync disabled by {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.Stop);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userAnimText);
                }

                if (vfxDisabled)
                {
                    var userVFXText = LF("VfxDisabledBy", "VFX sync disabled by {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.Circle);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userVFXText);
                }

                ImGui.EndTooltip();
            }
            ImGui.SameLine();
        }


        if (showPlus)
        {
            ImGui.SetCursorPosY(originalY);

            if (_uiSharedService.IconButton(FontAwesomeIcon.Plus))
            {
                _ = _apiController.UserAddPair(new UserDto(new(_pair.UserData.UID)));
            }
            UiSharedService.AttachToolTip(LF("PairIndividually", "Pair with {0} individually", entryUID));
            ImGui.SameLine();
        }
        if (showPause)
        {
            //rightSidePos -= pauseIconSize.X + spacingX;
            ImGui.SetCursorPosY(originalY);

            if (_uiSharedService.IconButton(pauseIcon))
            {
                if (_pair.UserPair != null)
                {
                    var perm = _pair.UserPair.OwnPermissions;
                    perm.SetPaused(!perm.IsPaused());
                    _ = _apiController.UserSetPairPermissions(new(_pair.UserData, perm));
                }
                else
                {
                    var groupPerm = _fullInfoDto.GroupUserPermissions;
                    groupPerm.SetPaused(!groupPerm.IsPaused());
                    _ = _apiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(
                        _group.Group,
                        _pair.UserData,
                        groupPerm
                    ));
                }
            }

            UiSharedService.AttachToolTip(!_fullInfoDto.GroupUserPermissions.IsPaused()
                ? LF("PausePairing", "Pause pairing with {0}", entryUID)
                : LF("ResumePairing", "Resume pairing with {0}", entryUID));
            ImGui.SameLine();

        }
        if (showBars)
        {
            ImGui.SetCursorPosY(originalY);

            if (_uiSharedService.IconButton(FontAwesomeIcon.Bars))
            {
                ImGui.OpenPopup("Popup");
            }
        }
        if (ImGui.BeginPopup("Popup"))
        {
            if ((userIsModerator || userIsOwner) && !(entryIsMod || entryIsOwner))
            {
                var pinText = entryIsPinned ? L("UnpinUser", "Unpin user") : L("PinUser", "Pin user");
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Thumbtack, pinText))
                {
                    ImGui.CloseCurrentPopup();
                    var userInfo = _fullInfoDto.GroupPairStatusInfo ^ GroupUserInfo.IsPinned;
                    _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(_fullInfoDto.Group, _fullInfoDto.User, userInfo));
                }
                UiSharedService.AttachToolTip(L("PinUserTooltip", "Pin this user to the Syncshell. Pinned users will not be deleted in case of a manually initiated Syncshell clean"));
                
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, L("RemoveUser", "Remove user")) && UiSharedService.CtrlPressed())
                {
                    ImGui.CloseCurrentPopup();
                    _ = _apiController.GroupRemoveUser(_fullInfoDto);
                }

                UiSharedService.AttachToolTip(LF("RemoveUserTooltip", "Hold CTRL and click to remove user {0} from Syncshell", _pair.UserData.AliasOrUID));
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserSlash, L("BanUser", "Ban User")))
                {
                    ImGui.CloseCurrentPopup();
                    _mediator.Publish(new OpenBanUserPopupMessage(_pair, _group));
                }
                UiSharedService.AttachToolTip(L("BanUserTooltip", "Ban user from this Syncshell"));
            }

            if (userIsOwner)
            {
                string modText = entryIsMod ? L("DemodUser", "Demod user") : L("ModUser", "Mod user");
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserShield, modText) && UiSharedService.CtrlPressed())
                {
                    ImGui.CloseCurrentPopup();
                    var userInfo = _fullInfoDto.GroupPairStatusInfo ^ GroupUserInfo.IsModerator;
                    _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(_fullInfoDto.Group, _fullInfoDto.User, userInfo));
                }
                UiSharedService.AttachToolTip(LF("ModStatusTooltip", "Hold CTRL to change the moderator status for {0}", _fullInfoDto.UserAliasOrUID) + Environment.NewLine +
                                              L("ModCapabilities", "Moderators can kick, ban/unban, pin/unpin users and clear the Syncshell."));
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Crown, L("TransferOwnership", "Transfer Ownership")) && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
                {
                    ImGui.CloseCurrentPopup();
                    _ = _apiController.GroupChangeOwnership(_fullInfoDto);
                }
                UiSharedService.AttachToolTip(LF("TransferOwnershipTooltip", "Hold CTRL and SHIFT and click to transfer ownership of this Syncshell to {0}", _fullInfoDto.UserAliasOrUID) + Environment.NewLine + L("OwnershipWarning", "WARNING: This action is irreversible."));
            }

            if (userIsOwner || (userIsModerator && !(entryIsMod || entryIsOwner)))
                ImGui.Separator();

                      if (_pair.UserPair == null)
            {
                var permissions = _fullInfoDto.GroupUserPermissions;

                var isDisableSounds = permissions.IsDisableSounds();
                var disableSoundsText = isDisableSounds ? L("EnableSound", "Enable sound sync") : L("DisableSound", "Disable sound sync");
                var disableSoundsIcon = isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute;
                if (_uiSharedService.IconTextButton(disableSoundsIcon, disableSoundsText))
                {
                    ImGui.CloseCurrentPopup();
                    permissions.SetDisableSounds(!isDisableSounds);
                    _ = _apiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(
                        _group.Group,
                        _pair.UserData,
                        permissions
                    ));
                }
                UiSharedService.AttachToolTip(L("SoundPermission", "Sets your allowance for sound synchronization for this Syncshell member.") +
                                              Environment.NewLine + L("PermissionApplies", "Disabling applies even without an individual pair."));

                var isDisableAnims = permissions.IsDisableAnimations();
                var disableAnimsText = isDisableAnims ? L("EnableAnimations", "Enable animation sync") : L("DisableAnimations", "Disable animation sync");
                var disableAnimsIcon = isDisableAnims ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop;
                if (_uiSharedService.IconTextButton(disableAnimsIcon, disableAnimsText))
                {
                    ImGui.CloseCurrentPopup();
                    permissions.SetDisableAnimations(!isDisableAnims);
                    _ = _apiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(
                        _group.Group,
                        _pair.UserData,
                        permissions
                    ));
                }
                UiSharedService.AttachToolTip(L("AnimationPermission", "Sets your allowance for animation synchronization for this Syncshell member.") +
                                              Environment.NewLine + L("PermissionApplies", "Disabling applies even without an individual pair."));

                var isDisableVfx = permissions.IsDisableVFX();
                var disableVfxText = isDisableVfx ? L("EnableVfx", "Enable VFX sync") : L("DisableVfx", "Disable VFX sync");
                var disableVfxIcon = isDisableVfx ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle;
                if (_uiSharedService.IconTextButton(disableVfxIcon, disableVfxText))
                {
                    ImGui.CloseCurrentPopup();
                    permissions.SetDisableVFX(!isDisableVfx);
                    _ = _apiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(
                        _group.Group,
                        _pair.UserData,
                        permissions
                    ));
                }
                UiSharedService.AttachToolTip(L("VfxPermission", "Sets your allowance for VFX synchronization for this Syncshell member.") +
                                              Environment.NewLine + L("PermissionApplies", "Disabling applies even without an individual pair."));

                ImGui.Separator();
            }

            if (_pair.IsVisible)
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Eye, L("TargetPlayer", "Target player")))
                {
                    _mediator.Publish(new TargetPairMessage(_pair));
                    ImGui.CloseCurrentPopup();
                }
            }
            if (!_pair.IsPaused)
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.User, L("OpenProfile", "Open Profile")))
                {
                    _displayHandler.OpenProfile(_pair);
                    ImGui.CloseCurrentPopup();
                }
            }
            if (_pair.IsVisible)
            {
#if DEBUG
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PersonCircleQuestion, L("OpenAnalysis", "Open Analysis")))
                {
                    _displayHandler.OpenAnalysis(_pair);
                    ImGui.CloseCurrentPopup();
                }
#endif
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, L("ReloadLastData", "Reload last data")))
                {
                    _pair.ApplyLastReceivedData(forced: true);
                    ImGui.CloseCurrentPopup();
                }
                UiSharedService.AttachToolTip(L("ReloadTooltip", "This reapplies the last received character data to this character"));
            }
            ImGui.EndPopup();
        }

        return pos - spacing;
    }
    
    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"DrawGroupPair.{key}", fallback);
    }

    private string LF(string key, string fallback, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, L(key, fallback), args);
    }
}