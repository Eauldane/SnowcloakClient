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
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Handlers;
using Snowcloak.WebAPI;
using System.Numerics;
using System;

namespace Snowcloak.UI.Components;

public class DrawGroupPair : DrawPairBase
{
    private readonly SnowMediator _mediator;
    private readonly GroupPairFullInfoDto _fullInfoDto;
    private readonly GroupFullInfoDto _group;
    private readonly CharaDataManager _charaDataManager;
    private readonly SnowcloakConfigService _configService;
    public long VRAMUsage { get; set; }

    public DrawGroupPair(string id, Pair entry, ApiController apiController,
        SnowMediator snowMediator, GroupFullInfoDto group, GroupPairFullInfoDto fullInfoDto,
        UidDisplayHandler handler, CharaDataManager charaDataManager,
        SnowcloakConfigService configService)
        : base(id, entry, apiController, handler)
    {
        _group = group;
        _fullInfoDto = fullInfoDto;
        _mediator = snowMediator;
        _charaDataManager = charaDataManager;
        _configService = configService;
    }

    private bool IsPausedByYou()
    {
        if (PairEntry.UserPair != null)
        {
            return PairEntry.UserPair.OwnPermissions.IsPaused();
        }

        return _group.GroupUserPermissions.IsPaused() || _fullInfoDto.OwnGroupUserPermissions.IsPaused();
    }

    private bool IsPausedByOther()
    {
        if (PairEntry.UserPair != null)
        {
            return PairEntry.UserPair.OtherPermissions.IsPaused();
        }

        return _fullInfoDto.OtherGroupUserPermissions.IsPaused();
    }

    protected override void DrawLeftSide(float textPosY, float originalY)
    {
        var entryUID = PairEntry.UserData.AliasOrUID;
        var entryIsMod = _fullInfoDto.GroupPairStatusInfo.IsModerator();
        var entryIsOwner = string.Equals(PairEntry.UserData.UID, _group.OwnerUID, StringComparison.Ordinal);
        var entryIsPinned = _fullInfoDto.GroupPairStatusInfo.IsPinned();
        var pausedByYou = IsPausedByYou();
        var pausedByOther = IsPausedByOther();
        var showAsOffline = pausedByOther && !pausedByYou;
        var isOnlineForDisplay = PairEntry.IsOnline && !showAsOffline;
        var isVisibleForDisplay = PairEntry.IsVisible && !showAsOffline;
        var presenceIcon = isVisibleForDisplay ? FontAwesomeIcon.Eye : (isOnlineForDisplay ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink);
        var presenceColor = (isOnlineForDisplay || isVisibleForDisplay) ? SnowcloakColours.OnlineBlue : ImGuiColors.DalamudRed;
        var presenceText = string.Format(CultureInfo.CurrentCulture, "{0} is offline", entryUID);
        
        if (pausedByYou)
        {
            presenceIcon = FontAwesomeIcon.PauseCircle;
            presenceColor = ImGuiColors.DalamudYellow;
            presenceText = string.Format(CultureInfo.CurrentCulture, "Pairing status with {0} is paused", entryUID);
        }

        if (!pausedByYou && isOnlineForDisplay && !isVisibleForDisplay)
            presenceText = string.Format(CultureInfo.CurrentCulture, "{0} is online", entryUID);
        else if (!pausedByYou && isOnlineForDisplay && isVisibleForDisplay)
            presenceText = string.Format(CultureInfo.CurrentCulture, "{0} is visible: {1}{2}Click to target this player", entryUID, PairEntry.PlayerName, Environment.NewLine);

        if (!pausedByYou && SyncshellMemberLabelUi.TryGetPresenceOverride(_fullInfoDto.MemberLabels, out var labelIcon, out var labelColor, out var labelTooltip))
        {
            presenceIcon = labelIcon;
            presenceColor = (isOnlineForDisplay || isVisibleForDisplay) ? labelColor : ImGuiColors.DalamudRed;
            presenceText += ElezenImgui.TooltipSeparator + Environment.NewLine + labelTooltip;
        }

        ImGui.SetCursorPosY(textPosY);
        ImGui.PushFont(UiBuilder.IconFont);
        ElezenImgui.ColouredText(presenceIcon.ToIconString(), presenceColor);
        ImGui.PopFont();
        if (!pausedByYou && isVisibleForDisplay)
        {
            if (ImGui.IsItemClicked())
            {
                _mediator.Publish(new TargetPairMessage(PairEntry));
            }
            presenceText = AppendModsInfoTooltip(presenceText);
        }

        if (PairEntry.IsAutoPaused && !string.IsNullOrEmpty(PairEntry.AutoPauseTooltip))
        {
            presenceText += ElezenImgui.TooltipSeparator + PairEntry.AutoPauseTooltip;
        }
        ElezenImgui.AttachTooltip(presenceText);

        if (entryIsOwner)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Crown.ToIconString());
            ImGui.PopFont();
            ElezenImgui.AttachTooltip("User is owner of this Syncshell");
        }
        else if (entryIsMod)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.UserShield.ToIconString());
            ImGui.PopFont();
            ElezenImgui.AttachTooltip("User is moderator of this Syncshell");
        }
        else if (entryIsPinned)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Thumbtack.ToIconString());
            ImGui.PopFont();
            ElezenImgui.AttachTooltip("User is pinned in this Syncshell");
        }

        if (PairEntry.IsAutoPaused && !string.IsNullOrEmpty(PairEntry.AutoPauseTooltip))
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textPosY);
            using var warningColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.ExclamationTriangle.ToIconString());
            ImGui.PopFont();
            ElezenImgui.AttachTooltip(PairEntry.AutoPauseTooltip);
        }
    }

    protected override float DrawRightSide(float textPosY, float originalY)
    {
        var pausedByYou = IsPausedByYou();
        var pausedByOther = IsPausedByOther();
        var pauseIcon = pausedByYou ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var actionSpacing = spacingX + (6f * ImGuiHelpers.GlobalScale);
        var entryUID = _fullInfoDto.UserAliasOrUID;
        var ownSoundsDisabled = _fullInfoDto.OwnGroupUserPermissions.IsDisableSounds();
        var ownAnimDisabled = _fullInfoDto.OwnGroupUserPermissions.IsDisableAnimations();
        var ownVfxDisabled = _fullInfoDto.OwnGroupUserPermissions.IsDisableVFX();
        var soundsDisabled = _fullInfoDto.OtherGroupUserPermissions.IsDisableSounds();
        var animDisabled = _fullInfoDto.OtherGroupUserPermissions.IsDisableAnimations();
        var vfxDisabled = _fullInfoDto.OtherGroupUserPermissions.IsDisableVFX();
        var individualSoundsDisabled = (PairEntry.UserPair?.OwnPermissions.IsDisableSounds() ?? false) || (PairEntry.UserPair?.OtherPermissions.IsDisableSounds() ?? false);
        var individualAnimDisabled = (PairEntry.UserPair?.OwnPermissions.IsDisableAnimations() ?? false) || (PairEntry.UserPair?.OtherPermissions.IsDisableAnimations() ?? false);
        var individualVFXDisabled = (PairEntry.UserPair?.OwnPermissions.IsDisableVFX() ?? false) || (PairEntry.UserPair?.OtherPermissions.IsDisableVFX() ?? false);

        bool showShared = _charaDataManager.SharedWithYouData.TryGetValue(PairEntry.UserData, out var sharedData);
        bool ownGroupDisabled = ownSoundsDisabled || ownAnimDisabled || ownVfxDisabled;
        bool otherGroupDisabled = soundsDisabled || animDisabled || vfxDisabled;
        bool showInfo = individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled || ownGroupDisabled || otherGroupDisabled;
        bool showPlus = PairEntry.UserPair == null;
        bool showBars = true;
        bool showPause = true; 
        var permIcon = (individualAnimDisabled || individualSoundsDisabled || individualVFXDisabled || ownGroupDisabled) ? FontAwesomeIcon.ExclamationTriangle
            : (otherGroupDisabled ? FontAwesomeIcon.InfoCircle : FontAwesomeIcon.None);
        var runningIconWidth = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Running).X;
        var infoIconWidth = ElezenImgui.GetIconSize(permIcon).X;
        var plusButtonWidth = RowActionButtonSize.X;
        var pauseButtonWidth = RowActionButtonSize.X;

        var barButtonWidth = RowActionButtonSize.X;
        ImGui.PushFont(UiBuilder.IconFont);
        var inlineIconHeight = ImGui.CalcTextSize(FontAwesomeIcon.InfoCircle.ToIconString()).Y;
        ImGui.PopFont();
        var inlineIconPosY = originalY + Math.Max(0f, (RowActionButtonSize.Y - inlineIconHeight) * 0.5f);
        var pos = ImGui.GetWindowContentRegionMin().X + ElezenImgui.GetWindowContentRegionWidth() + actionSpacing
            - (showShared ? (runningIconWidth + actionSpacing) : 0)
            - (showInfo ? (infoIconWidth + actionSpacing) : 0)
            - (showPlus ? (plusButtonWidth + actionSpacing) : 0)
            - (showPause ? (pauseButtonWidth + actionSpacing) : 0)
            - (showBars ? (barButtonWidth + actionSpacing) : 0);

        ImGui.SameLine(pos);

        using var actionSpacingStyle = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(actionSpacing, ImGui.GetStyle().ItemSpacing.Y));

        if (showShared)
        {
            ImGui.SetCursorPosY(inlineIconPosY);
            ElezenImgui.ShowIcon(FontAwesomeIcon.Running);

            ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture,"This user has shared {0} Character Data Sets with you.", sharedData!.Count) + ElezenImgui.TooltipSeparator
                + "Click to open the Character Data Hub and show the entries.");

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _mediator.Publish(new OpenCharaDataHubWithFilterMessage(PairEntry.UserData));
            }
            ImGui.SameLine();
        }

        if (individualAnimDisabled || individualSoundsDisabled)
        {
            ImGui.SetCursorPosY(inlineIconPosY);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            ElezenImgui.ShowIcon(permIcon);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                DrawIndividualPermissionsTooltipBody(individualSoundsDisabled, individualAnimDisabled, individualVFXDisabled);
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
        }
        else if (ownGroupDisabled)
        {
            ImGui.SetCursorPosY(inlineIconPosY);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            ElezenImgui.ShowIcon(permIcon);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();

                ImGui.TextUnformatted("Your Syncshell member permissions");

                if (ownSoundsDisabled)
                {
                    ElezenImgui.ShowIcon(FontAwesomeIcon.VolumeOff);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "Sound sync disabled with {0}", PairEntry.UserData.AliasOrUID));
                }

                if (ownAnimDisabled)
                {
                    ElezenImgui.ShowIcon(FontAwesomeIcon.Stop);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "Animation sync disabled with {0}", PairEntry.UserData.AliasOrUID));
                }

                if (ownVfxDisabled)
                {
                    ElezenImgui.ShowIcon(FontAwesomeIcon.Circle);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "VFX sync disabled with {0}", PairEntry.UserData.AliasOrUID));
                }

                ImGui.EndTooltip();
            }
            ImGui.SameLine();
        }
        else if (otherGroupDisabled)
        {
            ImGui.SetCursorPosY(inlineIconPosY);
            ElezenImgui.ShowIcon(permIcon);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();

                ImGui.TextUnformatted("Syncshell User permissions");
                
                if (soundsDisabled)
                {
                    var userSoundsText = string.Format(CultureInfo.CurrentCulture, "Sound sync disabled by {0}", PairEntry.UserData.AliasOrUID);
                    ElezenImgui.ShowIcon(FontAwesomeIcon.VolumeOff);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userSoundsText);
                }

                if (animDisabled)
                {
                    var userAnimText = string.Format(CultureInfo.CurrentCulture, "Animation sync disabled by {0}", PairEntry.UserData.AliasOrUID);
                    ElezenImgui.ShowIcon(FontAwesomeIcon.Stop);
                    ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                    ImGui.TextUnformatted(userAnimText);
                }

                if (vfxDisabled)
                {
                    var userVFXText = string.Format(CultureInfo.CurrentCulture, "VFX sync disabled by {0}", PairEntry.UserData.AliasOrUID);
                    ElezenImgui.ShowIcon(FontAwesomeIcon.Circle);
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

            if (DrawRowActionButton(FontAwesomeIcon.Plus, "pair-individual"))
            {
                _ = ApiController.UserAddPair(new UserDto(new(PairEntry.UserData.UID)));
            }
            ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "Pair with {0} individually", entryUID));
            ImGui.SameLine();
        }
        if (showPause)
        {
            ImGui.SetCursorPosY(originalY);

            if (DrawRowActionButton(pauseIcon, "pause"))
            {
                if (PairEntry.UserPair != null)
                {
                    var perm = PairEntry.UserPair.OwnPermissions;
                    perm.SetPaused(!perm.IsPaused());
                    _ = ApiController.UserSetPairPermissions(new(PairEntry.UserData, perm));
                }
                else
                {
                    var groupPerm = _fullInfoDto.OwnGroupUserPermissions;
                    groupPerm.SetPaused(!pausedByYou);
                    _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(
                        _group.Group,
                        PairEntry.UserData,
                        groupPerm
                    ));
                }
            }

            ElezenImgui.AttachTooltip(!pausedByYou
                ? string.Format(CultureInfo.CurrentCulture, "Pause pairing with {0}", entryUID)
                : string.Format(CultureInfo.CurrentCulture, "Resume pairing with {0}", entryUID));
            ImGui.SameLine();

        }
        if (showBars)
        {
            ImGui.SetCursorPosY(originalY);

            if (DrawRowActionButton(FontAwesomeIcon.Bars, "menu", SnowcloakColours.CompactTextMuted))
            {
                ImGui.OpenPopup("Popup");
            }
            ElezenImgui.AttachTooltip("More actions");
        }
        if (ImGui.BeginPopup("Popup"))
        {
            if (PairEntry.UserPair == null)
            {
                var permissions = _fullInfoDto.OwnGroupUserPermissions;

                var isDisableSounds = permissions.IsDisableSounds();
                var disableSoundsText = isDisableSounds ? "Enable sound sync" : "Disable sound sync";
                var disableSoundsIcon = isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute;
                if (ElezenImgui.ShowIconButton(disableSoundsIcon, disableSoundsText))
                {
                    ImGui.CloseCurrentPopup();
                    permissions.SetDisableSounds(!isDisableSounds);
                    _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(
                        _group.Group,
                        PairEntry.UserData,
                        permissions
                    ));
                }
                ElezenImgui.AttachTooltip("Sets your allowance for sound synchronization for this Syncshell member." +
                                              Environment.NewLine + "Disabling applies even without an individual pair.");

                var isDisableAnims = permissions.IsDisableAnimations();
                var disableAnimsText = isDisableAnims ? "Enable animation sync" : "Disable animation sync";
                var disableAnimsIcon = isDisableAnims ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop;
                if (ElezenImgui.ShowIconButton(disableAnimsIcon, disableAnimsText))
                {
                    ImGui.CloseCurrentPopup();
                    permissions.SetDisableAnimations(!isDisableAnims);
                    _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(
                        _group.Group,
                        PairEntry.UserData,
                        permissions
                    ));
                }
                ElezenImgui.AttachTooltip("Sets your allowance for animation synchronization for this Syncshell member." +
                                              Environment.NewLine +"Disabling applies even without an individual pair.");

                var isDisableVfx = permissions.IsDisableVFX();
                var disableVfxText = isDisableVfx ? "Enable VFX sync" : "Disable VFX sync";
                var disableVfxIcon = isDisableVfx ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle;
                if (ElezenImgui.ShowIconButton(disableVfxIcon, disableVfxText))
                {
                    ImGui.CloseCurrentPopup();
                    permissions.SetDisableVFX(!isDisableVfx);
                    _ = ApiController.GroupChangeIndividualPermissionState(new GroupPairUserPermissionDto(
                        _group.Group,
                        PairEntry.UserData,
                        permissions
                    ));
                }
                ElezenImgui.AttachTooltip("Sets your allowance for VFX synchronization for this Syncshell member." +
                                              Environment.NewLine + "Disabling applies even without an individual pair.");

                ImGui.Separator();
            }

            if (PairEntry.IsVisible && ElezenImgui.ShowIconButton(FontAwesomeIcon.Eye, "Target player"))
            {
                _mediator.Publish(new TargetPairMessage(PairEntry));
                ImGui.CloseCurrentPopup();
            }
            if ((!PairEntry.IsPaused || (pausedByOther && !pausedByYou)) && ElezenImgui.ShowIconButton(FontAwesomeIcon.User, "Open Profile"))
            {
                DisplayHandler.OpenProfile(PairEntry);
                ImGui.CloseCurrentPopup();
            }
            if (_configService.Current.EnableDebugFeatures
                && ElezenImgui.ShowIconButton(FontAwesomeIcon.QuestionCircle, "Why am I not seeing this user?"))
            {
                _mediator.Publish(new OpenSyncTroubleshootingWindow(PairEntry));
                ImGui.CloseCurrentPopup();
            }
            if (_configService.Current.EnableDebugFeatures)
            {
                ElezenImgui.AttachTooltip("Open a local diagnostic report for this user.");
            }
            if (PairEntry.IsVisible)
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.PersonCircleQuestion,  "Open Analysis"))
                {
                    DisplayHandler.OpenAnalysis(PairEntry);
                    ImGui.CloseCurrentPopup();
                }
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Sync, "Reload last data"))
                {
                    PairEntry.ApplyLastReceivedData(forced: true);
                    ImGui.CloseCurrentPopup();
                }
                ElezenImgui.AttachTooltip("This reapplies the last received character data to this character");
            }
            ImGui.EndPopup();
        }

        return pos - spacingX;
    }
}
