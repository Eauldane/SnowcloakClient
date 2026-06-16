using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Handlers;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Numerics;

namespace Snowcloak.UI.Components;

public class DrawUserPair : DrawPairBase
{
    private readonly SnowMediator _mediator;
    private readonly SelectGroupForPairUi _selectGroupForPairUi;
    private readonly CharaDataManager _charaDataManager;
    private readonly SnowcloakConfigService _configService;
    public long VramUsage { get; set; }

    public DrawUserPair(string id, Pair entry, UidDisplayHandler displayHandler, ApiController apiController,
        SnowMediator snowMediator, SelectGroupForPairUi selectGroupForPairUi,
        CharaDataManager charaDataManager, SnowcloakConfigService configService)
        : base(id, entry, apiController, displayHandler)
    {
        if (PairEntry.UserPair == null) throw new ArgumentException("Pair must be UserPair", nameof(entry));
        PairEntry = entry;
        _selectGroupForPairUi = selectGroupForPairUi;
        _mediator = snowMediator;
        _charaDataManager = charaDataManager;
        _configService = configService;
    }

    public bool IsOnline => PairEntry.IsOnline;
    public bool IsVisible => PairEntry.IsVisible;
    public UserPairDto UserPair => PairEntry.UserPair!;

    protected override void DrawLeftSide(float textPosY, float originalY)
    {
        FontAwesomeIcon connectionIcon;
        Vector4 connectionColor;
        string connectionText;
        bool isPaused = PairEntry.IsAutoPaused || PairEntry.UserPair!.OwnPermissions.IsPaused() || PairEntry.UserPair!.OtherPermissions.IsPaused();

        if (PairEntry.UserPair!.OwnPermissions.IsPaired() && PairEntry.UserPair!.OtherPermissions.IsPaired())
        {
            connectionIcon = FontAwesomeIcon.Snowflake;
            connectionText = string.Format(CultureInfo.CurrentCulture, "You are paired with {0}{1}", PairEntry.UserData.AliasOrUID, PairEntry.IsChatOnly ? " (chat only)" : string.Empty);
            connectionColor = PairEntry.IsOnline ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactOffline;
        }
        else
        {
            connectionIcon = FontAwesomeIcon.ArrowUp;
            connectionText = string.Format(CultureInfo.CurrentCulture, "{0} has not added you back", PairEntry.UserData.AliasOrUID);
            connectionColor = ImGuiColors.DalamudRed;
        }
        if (PairEntry is { IsOnline: true, IsVisible: true })
        {
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            ElezenImgui.ColouredText(FontAwesomeIcon.Eye.ToIconString(), isPaused ? SnowcloakColours.CompactOffline : SnowcloakColours.OnlineBlue);
            if (ImGui.IsItemClicked())
            {
                _mediator.Publish(new TargetPairMessage(PairEntry));
            }
            ImGui.PopFont();
            var visibleTooltip = string.Format(CultureInfo.CurrentCulture, "{0} is visible: {1}{2}Click to target this player", PairEntry.UserData.AliasOrUID, PairEntry.PlayerName!, Environment.NewLine);
            visibleTooltip = AppendModsInfoTooltip(visibleTooltip);

            if (PairEntry.IsAutoPaused && !string.IsNullOrEmpty(PairEntry.AutoPauseTooltip))
            {
                visibleTooltip += ElezenImgui.TooltipSeparator + PairEntry.AutoPauseTooltip;
            }

            ElezenImgui.AttachTooltip(string.IsNullOrEmpty(connectionText)
                ? visibleTooltip
                : connectionText + ElezenImgui.TooltipSeparator + visibleTooltip);

            if (PairEntry.IsAutoPaused && !string.IsNullOrEmpty(PairEntry.AutoPauseTooltip))
            {
                ImGui.SameLine();
                using var warningColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.TextUnformatted(FontAwesomeIcon.ExclamationTriangle.ToIconString());
                ImGui.PopFont();
                ElezenImgui.AttachTooltip(PairEntry.AutoPauseTooltip);
            }
            return;
        }
        
        ImGui.SetCursorPosY(textPosY);
        ImGui.PushFont(UiBuilder.IconFont);
        ElezenImgui.ColouredText(connectionIcon.ToIconString(), connectionColor);
        ImGui.PopFont();
        if (PairEntry.IsAutoPaused && !string.IsNullOrEmpty(PairEntry.AutoPauseTooltip))
        {
            connectionText += ElezenImgui.TooltipSeparator + PairEntry.AutoPauseTooltip;
        }
        ElezenImgui.AttachTooltip(connectionText);

        if (PairEntry.IsAutoPaused && !string.IsNullOrEmpty(PairEntry.AutoPauseTooltip))
        {
            ImGui.SameLine();
            using var warningColor = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.ExclamationTriangle.ToIconString());
            ImGui.PopFont();
            ElezenImgui.AttachTooltip(PairEntry.AutoPauseTooltip);
        }
    }



    protected override float DrawRightSide(float textPosY, float originalY)
    {
        var pauseIcon = PairEntry.UserPair!.OwnPermissions.IsPaused() ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var actionSize = RowActionButtonSize;
        var entryUID = PairEntry.UserData.AliasOrUID;
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var actionSpacing = spacingX + (6f * ImGuiHelpers.GlobalScale);
        var windowEndX = ImGui.GetWindowContentRegionMin().X + ElezenImgui.GetWindowContentRegionWidth();
        var rightSidePos = windowEndX - actionSize.X;

        // Flyout Menu
        ImGui.SameLine(rightSidePos);
        ImGui.SetCursorPosY(originalY);
        
        var flyoutMenuTitle = "User Flyout Menu";
        if (DrawRowActionButton(FontAwesomeIcon.Bars, "menu", SnowcloakColours.CompactTextMuted))
        {
            ImGui.OpenPopup(flyoutMenuTitle);
        }
        ElezenImgui.AttachTooltip("More actions");
        if (ImGui.BeginPopup(flyoutMenuTitle))
        {
            using (ImRaii.PushId($"buttons-{PairEntry.UserData.UID}")) DrawPairedClientMenu(PairEntry);
            ImGui.EndPopup();
        }

        // Pause (mutual pairs only)
        if (PairEntry.UserPair!.OwnPermissions.IsPaired() && PairEntry.UserPair!.OtherPermissions.IsPaired())
        {
            rightSidePos -= actionSize.X + actionSpacing;
            ImGui.SameLine(rightSidePos);
            ImGui.SetCursorPosY(originalY);
            if (DrawRowActionButton(pauseIcon, "pause"))
            {
                var perm = PairEntry.UserPair!.OwnPermissions;
                perm.SetPaused(!perm.IsPaused());
                _ = ApiController.UserSetPairPermissions(new(PairEntry.UserData, perm));
            }
            ElezenImgui.AttachTooltip(!PairEntry.UserPair!.OwnPermissions.IsPaused()
                ? string.Format(CultureInfo.CurrentCulture, "Pause pairing with {0}", entryUID)
                : string.Format(CultureInfo.CurrentCulture, "Resume pairing with {0}", entryUID));


            var individualSoundsDisabled = (PairEntry.UserPair?.OwnPermissions.IsDisableSounds() ?? false) || (PairEntry.UserPair?.OtherPermissions.IsDisableSounds() ?? false);
            var individualAnimDisabled = (PairEntry.UserPair?.OwnPermissions.IsDisableAnimations() ?? false) || (PairEntry.UserPair?.OtherPermissions.IsDisableAnimations() ?? false);
            var individualVFXDisabled = (PairEntry.UserPair?.OwnPermissions.IsDisableVFX() ?? false) || (PairEntry.UserPair?.OtherPermissions.IsDisableVFX() ?? false);

            // Icon for individually applied permissions
            if (individualSoundsDisabled || individualAnimDisabled || individualVFXDisabled)
            {
                var icon = FontAwesomeIcon.ExclamationTriangle;
                var iconwidth = ElezenImgui.GetIconButtonSize(icon);

                rightSidePos -= iconwidth.X + spacingX;
                ImGui.SameLine(rightSidePos);

                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                ElezenImgui.ShowIcon(icon);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    DrawIndividualPermissionsTooltipBody(individualSoundsDisabled, individualAnimDisabled, individualVFXDisabled);
                    ImGui.EndTooltip();
                }
            }
        }

        // Icon for shared character data
        if (_charaDataManager.SharedWithYouData.TryGetValue(PairEntry.UserData, out var sharedData))
        {
            var icon = FontAwesomeIcon.Running;
            var iconwidth = ElezenImgui.GetIconButtonSize(icon);
            rightSidePos -= iconwidth.X + spacingX;
            ImGui.SameLine(rightSidePos);
            ElezenImgui.ShowIcon(icon);

            ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "This user has shared {0} Character Data Sets with you.", sharedData.Count) + ElezenImgui.TooltipSeparator
                + "Click to open the Character Data Hub and show the entries.");

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _mediator.Publish(new OpenCharaDataHubWithFilterMessage(PairEntry.UserData));
            }
        }

        return rightSidePos - spacingX;
    }

    private void DrawPairedClientMenu(Pair entry)
    {
        if (entry.IsVisible && ElezenImgui.ShowIconButton(FontAwesomeIcon.Eye, "Target player"))
        {
            _mediator.Publish(new TargetPairMessage(entry));
            ImGui.CloseCurrentPopup();
        }
        if (!entry.IsPaused)
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.User,  "Open Profile"))
            {
                DisplayHandler.OpenProfile(entry);
                ImGui.CloseCurrentPopup();
            }
            ElezenImgui.AttachTooltip("Opens the profile for this user in a new window");
        }
        if (_configService.Current.EnableDebugFeatures
            && ElezenImgui.ShowIconButton(FontAwesomeIcon.QuestionCircle, "Why am I not seeing this user?"))
        {
            _mediator.Publish(new OpenSyncTroubleshootingWindow(entry));
            ImGui.CloseCurrentPopup();
        }
        if (_configService.Current.EnableDebugFeatures)
        {
            ElezenImgui.AttachTooltip("Open a local diagnostic report for this user.");
        }
        if (entry.IsVisible)
        {
#if DEBUG
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.PersonCircleQuestion, "Open Analysis"))
            {
                DisplayHandler.OpenAnalysis(PairEntry);
                ImGui.CloseCurrentPopup();
            }
#endif
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Sync, "Reload last data"))
            {
                entry.ApplyLastReceivedData(forced: true);
                ImGui.CloseCurrentPopup();
            }
            ElezenImgui.AttachTooltip("This reapplies the last received character data to this character");
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.PlayCircle, "Cycle pause state"))
        {
            _ = ApiController.CyclePause(entry.UserData);
            ImGui.CloseCurrentPopup();
        }
        var entryUID = entry.UserData.AliasOrUID;
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Folder, "Pair Groups"))
        {
            _selectGroupForPairUi.Open(entry);
        }
        ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "Choose pair groups for {0}", entryUID));

        var isDisableSounds = entry.UserPair!.OwnPermissions.IsDisableSounds();
        string disableSoundsText = isDisableSounds ? "Enable sound sync" : "Disable sound sync";
        var disableSoundsIcon = isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute;
        if (ElezenImgui.ShowIconButton(disableSoundsIcon, disableSoundsText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableSounds(!isDisableSounds);
            _ = ApiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        var isDisableAnims = entry.UserPair!.OwnPermissions.IsDisableAnimations();
        string disableAnimsText = isDisableAnims ? "Enable animation sync" : "Disable animation sync";
        var disableAnimsIcon = isDisableAnims ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop;
        if (ElezenImgui.ShowIconButton(disableAnimsIcon, disableAnimsText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableAnimations(!isDisableAnims);
            _ = ApiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        var isDisableVFX = entry.UserPair!.OwnPermissions.IsDisableVFX();
        string disableVFXText = isDisableVFX ? "Enable VFX sync" : "Disable VFX sync";
        var disableVFXIcon = isDisableVFX ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle;
        if (ElezenImgui.ShowIconButton(disableVFXIcon, disableVFXText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableVFX(!isDisableVFX);
            _ = ApiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Unpair Permanently") && ElezenImgui.CtrlPressed())
        {
            _ = ApiController.UserRemovePair(new(entry.UserData));
        }
        ElezenImgui.AttachTooltip(string.Format(CultureInfo.CurrentCulture, "Hold CTRL and click to unpair permanently from {0}", entryUID));
    }
    
}
