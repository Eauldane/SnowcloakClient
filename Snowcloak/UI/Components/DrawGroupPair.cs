using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Handlers;
using Snowcloak.WebAPI;
using System.Numerics;

namespace Snowcloak.UI.Components;

public class DrawGroupPair : DrawPairBase
{
    protected readonly SnowMediator _mediator;
    private readonly GroupPairFullInfoDto _fullInfoDto;
    private readonly GroupFullInfoDto _group;
    private readonly CharaDataManager _charaDataManager;
    public long VRAMUsage { get; set; }

    public DrawGroupPair(string id, Pair entry, ApiController apiController,
        SnowMediator snowMediator, GroupFullInfoDto group, GroupPairFullInfoDto fullInfoDto,
        UidDisplayHandler handler, UiSharedService uiSharedService, CharaDataManager charaDataManager)
        : base(id, entry, apiController, handler, uiSharedService)
    {
        _group = group;
        _fullInfoDto = fullInfoDto;
        _mediator = snowMediator;
        _charaDataManager = charaDataManager;
    }

    protected override void DrawLeftSide(float textPosY, float originalY)
    {
        var entryUID = _pair.UserData.AliasOrUID;
        var entryIsMod = _fullInfoDto.GroupPairStatusInfo.IsModerator();
        var entryIsOwner = string.Equals(_pair.UserData.UID, _group.OwnerUID, StringComparison.Ordinal);
        var entryIsPinned = _fullInfoDto.GroupPairStatusInfo.IsPinned();
        var presenceIcon = _pair.IsVisible ? FontAwesomeIcon.Eye : (_pair.IsOnline ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink);
        var presenceColor = (_pair.IsOnline || _pair.IsVisible) ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed;
        var presenceText = string.Format(CultureInfo.CurrentCulture, "{0} is offline", entryUID);
        
        ImGui.SetCursorPosY(textPosY);
        if (_pair.IsPaused)
        {
            presenceIcon = FontAwesomeIcon.Question;
            presenceColor = ImGuiColors.DalamudGrey;
            presenceText = string.Format(CultureInfo.CurrentCulture, "{0} online status is unknown (paused)", entryUID);
            
            ImGui.PushFont(UiBuilder.IconFont);
            ElezenImgui.ColouredText(FontAwesomeIcon.PauseCircle.ToIconString(), ImGuiColors.DalamudYellow);
            ImGui.PopFont();

            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "Pairing status with {0} is paused", entryUID));
        }
        else
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ElezenImgui.ColouredText(FontAwesomeIcon.Check.ToIconString(), ImGuiColors.ParsedGreen);
            ImGui.PopFont();

            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "You are paired with {0}", entryUID));
        }

        if (_pair.IsOnline && !_pair.IsVisible) presenceText = string.Format(CultureInfo.CurrentCulture, "{0} is online", entryUID);
        else if (_pair.IsOnline && _pair.IsVisible) presenceText = string.Format(CultureInfo.CurrentCulture, "{0} is visible: {1}{2}Click to target this player", entryUID, _pair.PlayerName, Environment.NewLine);

        ImGui.SameLine();
        ImGui.SetCursorPosY(textPosY);
        ImGui.PushFont(UiBuilder.IconFont);
        ElezenImgui.ColouredText(presenceIcon.ToIconString(), presenceColor);
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
                presenceText += ((!_pair.IsVisible) ? "(Last) " : string.Empty) + "Mods Info" + Environment.NewLine;
                presenceText += "Files Size: " + UiSharedService.ByteToString(_pair.LastAppliedDataBytes, true);
                if (_pair.LastAppliedApproximateVRAMBytes >= 0)
                {
                    presenceText += Environment.NewLine + "Approx. VRAM Usage: " + UiSharedService.ByteToString(_pair.LastAppliedApproximateVRAMBytes, true);
                }
                if (_pair.LastAppliedDataTris >= 0)
                {
                    presenceText += Environment.NewLine + "Triangle Count (excl. Vanilla): "
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
            UiSharedService.AttachToolTip("User is owner of this Syncshell");
        }
        else if (entryIsMod)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.UserShield.ToIconString());
            ImGui.PopFont();
            UiSharedService.AttachToolTip("User is moderator of this Syncshell");
        }
        else if (entryIsPinned)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Thumbtack.ToIconString());
            ImGui.PopFont();
            UiSharedService.AttachToolTip("User is pinned in this Syncshell");
        }
    }

    protected override float DrawRightSide(float textPosY, float originalY)
    {
        var pauseIcon = _pair.IsPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var actionSpacing = spacingX + (6f * ImGuiHelpers.GlobalScale);
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
        var permIcon = (individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled) ? FontAwesomeIcon.ExclamationTriangle
            : ((soundsDisabled || animDisabled || vfxDisabled) ? FontAwesomeIcon.InfoCircle : FontAwesomeIcon.None);
        var runningIconWidth = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Running).X;
        var infoIconWidth = UiSharedService.GetIconSize(permIcon).X;
        var plusButtonWidth = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus).X;
        var pauseButtonWidth = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Plus).X;

        var barButtonWidth = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Bars).X;
        var pos = ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() + actionSpacing
            - (showShared ? (runningIconWidth + actionSpacing) : 0)
            - (showInfo ? (infoIconWidth + actionSpacing) : 0)
            - (showPlus ? (plusButtonWidth + actionSpacing) : 0)
            - (showPause ? (pauseButtonWidth + actionSpacing) : 0)
            - (showBars ? (barButtonWidth + actionSpacing) : 0);

        ImGui.SameLine(pos);

        using var actionSpacingStyle = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(actionSpacing, ImGui.GetStyle().ItemSpacing.Y));

        if (showShared)
        {
            _uiSharedService.IconText(FontAwesomeIcon.Running);

            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture,"This user has shared {0} Character Data Sets with you.", sharedData!.Count) + UiSharedService.TooltipSeparator
                + "Click to open the Character Data Hub and show the entries.");

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

                ImGui.TextUnformatted("Individual User permissions");
                
                if (individualSoundsDisabled)
                {
                    var userSoundsText = string.Format(CultureInfo.CurrentCulture, "Sound sync disabled with {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.VolumeOff);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userSoundsText);
                    ImGui.NewLine();
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "You: {0}, They: {1}", _pair.UserPair!.OwnPermissions.IsDisableSounds() ? "Disabled" : "Enabled", _pair.UserPair!.OtherPermissions.IsDisableSounds() ? "Disabled" :  "Enabled"));
                }

                if (individualAnimDisabled)
                {
                    var userAnimText = string.Format(CultureInfo.CurrentCulture, "Animation sync disabled with {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.Stop);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userAnimText);
                    ImGui.NewLine();
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "You: {0}, They: {1}", _pair.UserPair!.OwnPermissions.IsDisableAnimations() ? "Disabled" : "Enabled", _pair.UserPair!.OtherPermissions.IsDisableAnimations() ? "Disabled" : "Enabled"));
                }

                if (individualVFXDisabled)
                {
                    var userVFXText = string.Format(CultureInfo.CurrentCulture, "VFX sync disabled with {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.Circle);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userVFXText);
                    ImGui.NewLine();
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "You: {0}, They: {1}", _pair.UserPair!.OwnPermissions.IsDisableVFX() ? "Disabled" : "Enabled", _pair.UserPair!.OtherPermissions.IsDisableVFX() ? "Disabled" : "Enabled"));
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

                ImGui.TextUnformatted("Syncshell User permissions");
                
                if (soundsDisabled)
                {
                    var userSoundsText = string.Format(CultureInfo.CurrentCulture, "Sound sync disabled by {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.VolumeOff);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userSoundsText);
                }

                if (animDisabled)
                {
                    var userAnimText = string.Format(CultureInfo.CurrentCulture, "Animation sync disabled by {0}", _pair.UserData.AliasOrUID);
                    _uiSharedService.IconText(FontAwesomeIcon.Stop);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userAnimText);
                }

                if (vfxDisabled)
                {
                    var userVFXText = string.Format(CultureInfo.CurrentCulture, "VFX sync disabled by {0}", _pair.UserData.AliasOrUID);
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
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "Pair with {0} individually", entryUID));
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
                ? string.Format(CultureInfo.CurrentCulture, "Pause pairing with {0}", entryUID)
                : string.Format(CultureInfo.CurrentCulture, "Resume pairing with {0}", entryUID));
            ImGui.SameLine();

        }
        if (showBars)
        {
            ImGui.SetCursorPosY(originalY);

            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey))
            {
                if (_uiSharedService.IconButton(FontAwesomeIcon.Bars))
                {
                    ImGui.OpenPopup("Popup");
                }
            }
            UiSharedService.AttachToolTip("More actions");
        }
        if (ImGui.BeginPopup("Popup"))
        {
            if ((userIsModerator || userIsOwner) && !(entryIsMod || entryIsOwner))
            {
                var pinText = entryIsPinned ? "Unpin user" : "Pin user";
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Thumbtack, pinText))
                {
                    ImGui.CloseCurrentPopup();
                    var userInfo = _fullInfoDto.GroupPairStatusInfo ^ GroupUserInfo.IsPinned;
                    _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(_fullInfoDto.Group, _fullInfoDto.User, userInfo));
                }
                UiSharedService.AttachToolTip("Pin this user to the Syncshell. Pinned users will not be deleted in case of a manually initiated Syncshell clean");
                
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Remove user") && UiSharedService.CtrlPressed())
                {
                    ImGui.CloseCurrentPopup();
                    _ = _apiController.GroupRemoveUser(_fullInfoDto);
                }

                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture,"Hold CTRL and click to remove user {0} from Syncshell", _pair.UserData.AliasOrUID));
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserSlash, "Ban User"))
                {
                    ImGui.CloseCurrentPopup();
                    _mediator.Publish(new OpenBanUserPopupMessage(_pair, _group));
                }
                UiSharedService.AttachToolTip("Ban user from this Syncshell");
            }

            if (userIsOwner)
            {
                string modText = entryIsMod ? "Demod user" :"Mod user";
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.UserShield, modText) && UiSharedService.CtrlPressed())
                {
                    ImGui.CloseCurrentPopup();
                    var userInfo = _fullInfoDto.GroupPairStatusInfo ^ GroupUserInfo.IsModerator;
                    _ = _apiController.GroupSetUserInfo(new GroupPairUserInfoDto(_fullInfoDto.Group, _fullInfoDto.User, userInfo));
                }
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "Hold CTRL to change the moderator status for {0}", _fullInfoDto.UserAliasOrUID) + Environment.NewLine +
                                              "Moderators can kick, ban/unban, pin/unpin users and clear the Syncshell.");
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Crown, "Transfer Ownership") && UiSharedService.CtrlPressed() && UiSharedService.ShiftPressed())
                {
                    ImGui.CloseCurrentPopup();
                    _ = _apiController.GroupChangeOwnership(_fullInfoDto);
                }
                UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "Hold CTRL and SHIFT and click to transfer ownership of this Syncshell to {0}", _fullInfoDto.UserAliasOrUID) + Environment.NewLine + "WARNING: This action is irreversible.");
            }

            if (userIsOwner || (userIsModerator && !(entryIsMod || entryIsOwner)))
                ImGui.Separator();

                      if (_pair.UserPair == null)
            {
                var permissions = _fullInfoDto.GroupUserPermissions;

                var isDisableSounds = permissions.IsDisableSounds();
                var disableSoundsText = isDisableSounds ? "Enable sound sync" : "Disable sound sync";
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
                UiSharedService.AttachToolTip("Sets your allowance for sound synchronization for this Syncshell member." +
                                              Environment.NewLine + "Disabling applies even without an individual pair.");

                var isDisableAnims = permissions.IsDisableAnimations();
                var disableAnimsText = isDisableAnims ? "Enable animation sync" : "Disable animation sync";
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
                UiSharedService.AttachToolTip("Sets your allowance for animation synchronization for this Syncshell member." +
                                              Environment.NewLine +"Disabling applies even without an individual pair.");

                var isDisableVfx = permissions.IsDisableVFX();
                var disableVfxText = isDisableVfx ? "Enable VFX sync" : "Disable VFX sync";
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
                UiSharedService.AttachToolTip("Sets your allowance for VFX synchronization for this Syncshell member." +
                                              Environment.NewLine + "Disabling applies even without an individual pair.");

                ImGui.Separator();
            }

            if (_pair.IsVisible)
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Eye, "Target player"))
                {
                    _mediator.Publish(new TargetPairMessage(_pair));
                    ImGui.CloseCurrentPopup();
                }
            }
            if (!_pair.IsPaused)
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.User, "Open Profile"))
                {
                    _displayHandler.OpenProfile(_pair);
                    ImGui.CloseCurrentPopup();
                }
            }
            if (_pair.IsVisible)
            {
#if DEBUG
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PersonCircleQuestion,  "Open Analysis"))
                {
                    _displayHandler.OpenAnalysis(_pair);
                    ImGui.CloseCurrentPopup();
                }
#endif
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, "Reload last data"))
                {
                    _pair.ApplyLastReceivedData(forced: true);
                    ImGui.CloseCurrentPopup();
                }
                UiSharedService.AttachToolTip("This reapplies the last received character data to this character");
            }
            ImGui.EndPopup();
        }

        return pos - spacingX;
    }
}
