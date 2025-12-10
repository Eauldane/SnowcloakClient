using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.User;
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
    protected readonly SnowMediator _mediator;
    private readonly SelectGroupForPairUi _selectGroupForPairUi;
    private readonly CharaDataManager _charaDataManager;
    public long VramUsage { get; set; }

    public DrawUserPair(string id, Pair entry, UidDisplayHandler displayHandler, ApiController apiController,
        SnowMediator snowMediator, SelectGroupForPairUi selectGroupForPairUi,
        UiSharedService uiSharedService, CharaDataManager charaDataManager)
        : base(id, entry, apiController, displayHandler, uiSharedService)
    {
        if (_pair.UserPair == null) throw new ArgumentException("Pair must be UserPair", nameof(entry));
        _pair = entry;
        _selectGroupForPairUi = selectGroupForPairUi;
        _mediator = snowMediator;
        _charaDataManager = charaDataManager;
    }

    public bool IsOnline => _pair.IsOnline;
    public bool IsVisible => _pair.IsVisible;
    public UserPairDto UserPair => _pair.UserPair!;

    protected override void DrawLeftSide(float textPosY, float originalY)
    {
        FontAwesomeIcon connectionIcon;
        Vector4 connectionColor;
        string connectionText;
        bool isPaused = _pair.IsAutoPaused || _pair.UserPair!.OwnPermissions.IsPaused() || _pair.UserPair!.OtherPermissions.IsPaused();

        if (_pair.UserPair!.OwnPermissions.IsPaired() && _pair.UserPair!.OtherPermissions.IsPaired())
        {
            connectionIcon = FontAwesomeIcon.Snowflake;
            connectionText = "You are paired with " + _pair.UserData.AliasOrUID;
            connectionColor = _pair.IsOnline ? Colours._snowcloakOnline : ImGuiColors.DalamudGrey;
        }
        else
        {
            connectionIcon = FontAwesomeIcon.ArrowUp;
            connectionText = _pair.UserData.AliasOrUID + " has not added you back";
            connectionColor = ImGuiColors.DalamudRed;
        }
        if (_pair is { IsOnline: true, IsVisible: true })
        {
            ImGui.SetCursorPosY(textPosY);
            ImGui.PushFont(UiBuilder.IconFont);
            UiSharedService.ColorText(FontAwesomeIcon.Eye.ToIconString(), isPaused ? ImGuiColors.DalamudGrey : Colours._snowcloakOnline);
            if (ImGui.IsItemClicked())
            {
                _mediator.Publish(new TargetPairMessage(_pair));
            }
            ImGui.PopFont();
            var visibleTooltip = _pair.UserData.AliasOrUID + " is visible: " + _pair.PlayerName! + Environment.NewLine + "Click to target this player";
            if (_pair.LastAppliedDataBytes >= 0)
            {
                visibleTooltip += UiSharedService.TooltipSeparator;
                visibleTooltip += ((!_pair.IsVisible) ? "(Last) " : string.Empty) + "Mods Info" + Environment.NewLine;
                visibleTooltip += "Files Size: " + UiSharedService.ByteToString(_pair.LastAppliedDataBytes, true);
                if (_pair.LastAppliedApproximateVRAMBytes >= 0)
                {
                    VramUsage = _pair.LastAppliedApproximateVRAMBytes;
                    visibleTooltip += Environment.NewLine + "Approx. VRAM Usage: " + UiSharedService.ByteToString(_pair.LastAppliedApproximateVRAMBytes, true);
                }
                if (_pair.LastAppliedDataTris >= 0)
                {
                    visibleTooltip += Environment.NewLine + "Triangle Count (excl. Vanilla): "
                        + (_pair.LastAppliedDataTris > 1000 ? (_pair.LastAppliedDataTris / 1000d).ToString("0.0'k'") : _pair.LastAppliedDataTris);
                }
            }

            UiSharedService.AttachToolTip(string.IsNullOrEmpty(connectionText)
                ? visibleTooltip
                : connectionText + UiSharedService.TooltipSeparator + visibleTooltip);
            return;
        }
        
        ImGui.SetCursorPosY(textPosY);
        ImGui.PushFont(UiBuilder.IconFont);
        UiSharedService.ColorText(connectionIcon.ToIconString(), connectionColor);
        ImGui.PopFont();
        UiSharedService.AttachToolTip(connectionText);
    }



    protected override float DrawRightSide(float textPosY, float originalY)
    {
        var pauseIcon = _pair.UserPair!.OwnPermissions.IsPaused() ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var pauseIconSize = _uiSharedService.GetIconButtonSize(pauseIcon);
        var barButtonSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Bars);
        var entryUID = _pair.UserData.AliasOrUID;
        var spacingX = ImGui.GetStyle().ItemSpacing.X;
        var windowEndX = ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth();
        var rightSidePos = windowEndX - barButtonSize.X;

        // Flyout Menu
        ImGui.SameLine(rightSidePos);
        ImGui.SetCursorPosY(originalY);

        if (_uiSharedService.IconButton(FontAwesomeIcon.Bars))
        {
            ImGui.OpenPopup("User Flyout Menu");
        }
        if (ImGui.BeginPopup("User Flyout Menu"))
        {
            using (ImRaii.PushId($"buttons-{_pair.UserData.UID}")) DrawPairedClientMenu(_pair);
            ImGui.EndPopup();
        }

        // Pause (mutual pairs only)
        if (_pair.UserPair!.OwnPermissions.IsPaired() && _pair.UserPair!.OtherPermissions.IsPaired())
        {
            rightSidePos -= pauseIconSize.X + spacingX;
            ImGui.SameLine(rightSidePos);
            ImGui.SetCursorPosY(originalY);
            if (_uiSharedService.IconButton(pauseIcon))
            {
                var perm = _pair.UserPair!.OwnPermissions;
                perm.SetPaused(!perm.IsPaused());
                _ = _apiController.UserSetPairPermissions(new(_pair.UserData, perm));
            }
            UiSharedService.AttachToolTip(!_pair.UserPair!.OwnPermissions.IsPaused()
                ? "Pause pairing with " + entryUID
                : "Resume pairing with " + entryUID);


            var individualSoundsDisabled = (_pair.UserPair?.OwnPermissions.IsDisableSounds() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableSounds() ?? false);
            var individualAnimDisabled = (_pair.UserPair?.OwnPermissions.IsDisableAnimations() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableAnimations() ?? false);
            var individualVFXDisabled = (_pair.UserPair?.OwnPermissions.IsDisableVFX() ?? false) || (_pair.UserPair?.OtherPermissions.IsDisableVFX() ?? false);

            // Icon for individually applied permissions
            if (individualSoundsDisabled || individualAnimDisabled || individualVFXDisabled)
            {
                var icon = FontAwesomeIcon.ExclamationTriangle;
                var iconwidth = _uiSharedService.GetIconButtonSize(icon);

                rightSidePos -= iconwidth.X + spacingX / 2f;
                ImGui.SameLine(rightSidePos);

                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                _uiSharedService.IconText(icon);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();

                    ImGui.TextUnformatted("Individual User permissions");

                    if (individualSoundsDisabled)
                    {
                        var userSoundsText = "Sound sync disabled with " + _pair.UserData.AliasOrUID;
                        _uiSharedService.IconText(FontAwesomeIcon.VolumeOff);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userSoundsText);
                        ImGui.NewLine();
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted("You: " + (_pair.UserPair!.OwnPermissions.IsDisableSounds() ? "Disabled" : "Enabled") + ", They: " + (_pair.UserPair!.OtherPermissions.IsDisableSounds() ? "Disabled" : "Enabled"));
                    }

                    if (individualAnimDisabled)
                    {
                        var userAnimText = "Animation sync disabled with " + _pair.UserData.AliasOrUID;
                        _uiSharedService.IconText(FontAwesomeIcon.Stop);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userAnimText);
                        ImGui.NewLine();
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted("You: " + (_pair.UserPair!.OwnPermissions.IsDisableAnimations() ? "Disabled" : "Enabled") + ", They: " + (_pair.UserPair!.OtherPermissions.IsDisableAnimations() ? "Disabled" : "Enabled"));
                    }

                    if (individualVFXDisabled)
                    {
                        var userVFXText = "VFX sync disabled with " + _pair.UserData.AliasOrUID;
                        _uiSharedService.IconText(FontAwesomeIcon.Circle);
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted(userVFXText);
                        ImGui.NewLine();
                        ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
                        ImGui.TextUnformatted("You: " + (_pair.UserPair!.OwnPermissions.IsDisableVFX() ? "Disabled" : "Enabled") + ", They: " + (_pair.UserPair!.OtherPermissions.IsDisableVFX() ? "Disabled" : "Enabled"));
                    }

                    ImGui.EndTooltip();
                }
            }
        }

        // Icon for shared character data
        if (_charaDataManager.SharedWithYouData.TryGetValue(_pair.UserData, out var sharedData))
        {
            var icon = FontAwesomeIcon.Running;
            var iconwidth = _uiSharedService.GetIconButtonSize(icon);
            rightSidePos -= iconwidth.X + spacingX / 2f;
            ImGui.SameLine(rightSidePos);
            _uiSharedService.IconText(icon);

            UiSharedService.AttachToolTip($"This user has shared {sharedData.Count} Character Data Sets with you." + UiSharedService.TooltipSeparator
                + "Click to open the Character Data Hub and show the entries.");

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _mediator.Publish(new OpenCharaDataHubWithFilterMessage(_pair.UserData));
            }
        }

        return rightSidePos - spacingX;
    }

    private void DrawPairedClientMenu(Pair entry)
    {
        if (entry.IsVisible)
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Eye, "Target player"))
            {
                _mediator.Publish(new TargetPairMessage(entry));
                ImGui.CloseCurrentPopup();
            }
        }
        if (!entry.IsPaused)
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.User, "Open Profile"))
            {
                _displayHandler.OpenProfile(entry);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("Opens the profile for this user in a new window");
        }
        if (entry.IsVisible)
        {
#if DEBUG
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.PersonCircleQuestion, "Open Analysis"))
            {
                _displayHandler.OpenAnalysis(_pair);
                ImGui.CloseCurrentPopup();
            }
#endif
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Sync, "Reload last data"))
            {
                entry.ApplyLastReceivedData(forced: true);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip("This reapplies the last received character data to this character");
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, "Cycle pause state"))
        {
            _ = _apiController.CyclePause(entry.UserData);
            ImGui.CloseCurrentPopup();
        }
        var entryUID = entry.UserData.AliasOrUID;
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Folder, "Pair Groups"))
        {
            _selectGroupForPairUi.Open(entry);
        }
        UiSharedService.AttachToolTip("Choose pair groups for " + entryUID);

        var isDisableSounds = entry.UserPair!.OwnPermissions.IsDisableSounds();
        string disableSoundsText = isDisableSounds ? "Enable sound sync" : "Disable sound sync";
        var disableSoundsIcon = isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute;
        if (_uiSharedService.IconTextButton(disableSoundsIcon, disableSoundsText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableSounds(!isDisableSounds);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        var isDisableAnims = entry.UserPair!.OwnPermissions.IsDisableAnimations();
        string disableAnimsText = isDisableAnims ? "Enable animation sync" : "Disable animation sync";
        var disableAnimsIcon = isDisableAnims ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop;
        if (_uiSharedService.IconTextButton(disableAnimsIcon, disableAnimsText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableAnimations(!isDisableAnims);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        var isDisableVFX = entry.UserPair!.OwnPermissions.IsDisableVFX();
        string disableVFXText = isDisableVFX ? "Enable VFX sync" : "Disable VFX sync";
        var disableVFXIcon = isDisableVFX ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle;
        if (_uiSharedService.IconTextButton(disableVFXIcon, disableVFXText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableVFX(!isDisableVFX);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Unpair Permanently") && UiSharedService.CtrlPressed())
        {
            _ = _apiController.UserRemovePair(new(entry.UserData));
        }
        UiSharedService.AttachToolTip("Hold CTRL and click to unpair permanently from " + entryUID);
    }
}