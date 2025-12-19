using System.Globalization;
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
using Snowcloak.Services.Localisation;

namespace Snowcloak.UI.Components;

public class DrawUserPair : DrawPairBase
{
    protected readonly SnowMediator _mediator;
    private readonly SelectGroupForPairUi _selectGroupForPairUi;
    private readonly CharaDataManager _charaDataManager;
    private readonly LocalisationService _localisationService;
    public long VramUsage { get; set; }

    public DrawUserPair(string id, Pair entry, UidDisplayHandler displayHandler, ApiController apiController,
        SnowMediator snowMediator, SelectGroupForPairUi selectGroupForPairUi,
        UiSharedService uiSharedService, CharaDataManager charaDataManager, LocalisationService localisationService)
        : base(id, entry, apiController, displayHandler, uiSharedService)
    {
        if (_pair.UserPair == null) throw new ArgumentException("Pair must be UserPair", nameof(entry));
        _pair = entry;
        _selectGroupForPairUi = selectGroupForPairUi;
        _mediator = snowMediator;
        _charaDataManager = charaDataManager;
        _localisationService = localisationService;
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
            connectionText = LF("PairedWith", "You are paired with {0}", _pair.UserData.AliasOrUID);
            connectionColor = _pair.IsOnline ? Colours._snowcloakOnline : ImGuiColors.DalamudGrey;
        }
        else
        {
            connectionIcon = FontAwesomeIcon.ArrowUp;
            connectionText = LF("NotAddedBack", "{0} has not added you back", _pair.UserData.AliasOrUID);
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
            var visibleTooltip = LF("VisibleTooltip", "{0} is visible: {1}{2}Click to target this player", _pair.UserData.AliasOrUID, _pair.PlayerName!, Environment.NewLine);
            if (_pair.LastAppliedDataBytes >= 0)
            {
                visibleTooltip += UiSharedService.TooltipSeparator;
                visibleTooltip += ((!_pair.IsVisible) ? L("LastPrefix", "(Last) ") : string.Empty) + L("ModsInfo", "Mods Info") + Environment.NewLine;
                visibleTooltip += L("FilesSize", "Files Size: ") + UiSharedService.ByteToString(_pair.LastAppliedDataBytes, true);
                if (_pair.LastAppliedApproximateVRAMBytes >= 0)
                {
                    VramUsage = _pair.LastAppliedApproximateVRAMBytes;
                    visibleTooltip += Environment.NewLine + L("ApproxVram", "Approx. VRAM Usage: ") + UiSharedService.ByteToString(_pair.LastAppliedApproximateVRAMBytes, true);
                }
                if (_pair.LastAppliedDataTris >= 0)
                {
                    visibleTooltip += Environment.NewLine + L("TriangleCount", "Triangle Count (excl. Vanilla): ")
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
        
        var flyoutMenuTitle = L("UserFlyoutMenu", "User Flyout Menu");
        if (_uiSharedService.IconButton(FontAwesomeIcon.Bars))
        {
            ImGui.OpenPopup(flyoutMenuTitle);
        }
        if (ImGui.BeginPopup(flyoutMenuTitle))
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
                ? LF("PausePairing", "Pause pairing with {0}", entryUID)
                : LF("ResumePairing", "Resume pairing with {0}", entryUID));


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

            UiSharedService.AttachToolTip(LF("SharedData", "This user has shared {0} Character Data Sets with you.", sharedData.Count) + UiSharedService.TooltipSeparator
                + L("OpenCharaHub", "Click to open the Character Data Hub and show the entries."));

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
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Eye, L("TargetPlayer", "Target player")))
            {
                _mediator.Publish(new TargetPairMessage(entry));
                ImGui.CloseCurrentPopup();
            }
        }
        if (!entry.IsPaused)
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.User, L("OpenProfile", "Open Profile")))
            {
                _displayHandler.OpenProfile(entry);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip(L("OpenProfileTooltip", "Opens the profile for this user in a new window"));
        }
        if (entry.IsVisible)
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
                entry.ApplyLastReceivedData(forced: true);
                ImGui.CloseCurrentPopup();
            }
            UiSharedService.AttachToolTip(L("ReloadTooltip", "This reapplies the last received character data to this character"));
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, L("CyclePause", "Cycle pause state")))
        {
            _ = _apiController.CyclePause(entry.UserData);
            ImGui.CloseCurrentPopup();
        }
        var entryUID = entry.UserData.AliasOrUID;
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Folder, L("PairGroups", "Pair Groups")))
        {
            _selectGroupForPairUi.Open(entry);
        }
        UiSharedService.AttachToolTip(LF("PairGroupsTooltip", "Choose pair groups for {0}", entryUID));

        var isDisableSounds = entry.UserPair!.OwnPermissions.IsDisableSounds();
        string disableSoundsText = isDisableSounds ? L("EnableSound", "Enable sound sync") : L("DisableSound", "Disable sound sync");
        var disableSoundsIcon = isDisableSounds ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute;
        if (_uiSharedService.IconTextButton(disableSoundsIcon, disableSoundsText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableSounds(!isDisableSounds);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        var isDisableAnims = entry.UserPair!.OwnPermissions.IsDisableAnimations();
        string disableAnimsText = isDisableAnims ? L("EnableAnimations", "Enable animation sync") : L("DisableAnimations", "Disable animation sync");
        var disableAnimsIcon = isDisableAnims ? FontAwesomeIcon.Running : FontAwesomeIcon.Stop;
        if (_uiSharedService.IconTextButton(disableAnimsIcon, disableAnimsText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableAnimations(!isDisableAnims);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        var isDisableVFX = entry.UserPair!.OwnPermissions.IsDisableVFX();
        string disableVFXText = isDisableVFX ? L("EnableVfx", "Enable VFX sync") : L("DisableVfx", "Disable VFX sync");
        var disableVFXIcon = isDisableVFX ? FontAwesomeIcon.Sun : FontAwesomeIcon.Circle;
        if (_uiSharedService.IconTextButton(disableVFXIcon, disableVFXText))
        {
            var permissions = entry.UserPair.OwnPermissions;
            permissions.SetDisableVFX(!isDisableVFX);
            _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, permissions));
        }

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, L("Unpair", "Unpair Permanently")) && UiSharedService.CtrlPressed())
        {
            _ = _apiController.UserRemovePair(new(entry.UserData));
        }
        UiSharedService.AttachToolTip(LF("UnpairTooltip", "Hold CTRL and click to unpair permanently from {0}", entryUID));
    }
    
    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"DrawUserPair.{key}", fallback);
    }

    private string LF(string key, string fallback, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, L(key, fallback), args);
    }
}