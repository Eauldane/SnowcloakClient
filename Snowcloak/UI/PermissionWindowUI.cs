using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Snowcloak.Services.Localisation;

namespace Snowcloak.UI;

public class PermissionWindowUI : WindowMediatorSubscriberBase
{
    public Pair Pair { get; init; }

    private readonly UiSharedService _uiSharedService;
    private readonly ApiController _apiController;
    private readonly LocalisationService _localisationService;
    private UserPermissions _ownPermissions;

    public PermissionWindowUI(ILogger<PermissionWindowUI> logger, Pair pair, SnowMediator mediator, UiSharedService uiSharedService,
        ApiController apiController, PerformanceCollectorService performanceCollectorService, LocalisationService localisationService)
        : base(logger, mediator,
            localisationService.GetString("PermissionWindowUI.WindowTitle", $"Permissions for {pair.UserData.AliasOrUID}") +
            "###SnowcloakSyncPermissions" + pair.UserData.UID,
            performanceCollectorService)
    {
        Pair = pair;
        _uiSharedService = uiSharedService;
        _apiController = apiController;
        _localisationService = localisationService;
        _ownPermissions = pair.UserPair?.OwnPermissions.DeepClone() ?? default;
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize;
        SizeConstraints = new()
        {
            MinimumSize = new(450, 100),
            MaximumSize = new(450, 500)
        };
        IsOpen = true;
    }

    protected override void DrawInternal()
    {
        var paused = _ownPermissions.IsPaused();
        var disableSounds = _ownPermissions.IsDisableSounds();
        var disableAnimations = _ownPermissions.IsDisableAnimations();
        var disableVfx = _ownPermissions.IsDisableVFX();
        var style = ImGui.GetStyle();
        var indentSize = ImGui.GetFrameHeight() + style.ItemSpacing.X;

        _uiSharedService.BigText(string.Format(L("Heading", "Permissions for {0}"), Pair.UserData.AliasOrUID));
        ImGuiHelpers.ScaledDummy(1f);

        if (Pair.UserPair == null)
            return;

        if (ImGui.Checkbox(L("PauseSync", "Pause Sync"), ref paused))
        {
            _ownPermissions.SetPaused(paused);
        }
        _uiSharedService.DrawHelpText(L("PauseSync.Help", "Pausing will completely cease any sync with this user.") + UiSharedService.TooltipSeparator
            + L("BidirectionalNote", "Note: this is bidirectional, either user pausing will cease sync completely."));
        var otherPerms = Pair.UserPair.OtherPermissions;

        var otherIsPaused = otherPerms.IsPaused();
        var otherDisableSounds = otherPerms.IsDisableSounds();
        var otherDisableAnimations = otherPerms.IsDisableAnimations();
        var otherDisableVFX = otherPerms.IsDisableVFX();

        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherIsPaused, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format(L("OtherPaused", "{0} has {1}paused you"), Pair.UserData.AliasOrUID, !otherIsPaused ? "not " : string.Empty));
        }

        ImGuiHelpers.ScaledDummy(0.5f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0.5f);

        if (ImGui.Checkbox(L("DisableSounds", "Disable Sounds"), ref disableSounds))
        {
            _ownPermissions.SetDisableSounds(disableSounds);
        }
        _uiSharedService.DrawHelpText(L("DisableSounds.Help", "Disabling sounds will remove all sounds synced with this user on both sides.") + UiSharedService.TooltipSeparator
            + L("BidirectionalNote", "Note: this is bidirectional, either user disabling sound sync will stop sound sync on both sides."));
        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherDisableSounds, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format(L("OtherDisableSounds", "{0} has {1}disabled sound sync with you"), Pair.UserData.AliasOrUID, !otherDisableSounds ? "not " : string.Empty));
        }

        if (ImGui.Checkbox(L("DisableAnimations", "Disable Animations"), ref disableAnimations))
        {
            _ownPermissions.SetDisableAnimations(disableAnimations);
        }
        _uiSharedService.DrawHelpText(L("DisableAnimations.Help", "Disabling animationss will remove all animations synced with this user on both sides.") + UiSharedService.TooltipSeparator
            + L("BidirectionalNote", "Note: this is bidirectional, either user disabling animation sync will stop animation sync on both sides."));
        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherDisableAnimations, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format(L("OtherDisableAnimations", "{0} has {1}disabled animation sync with you"), Pair.UserData.AliasOrUID, !otherDisableAnimations ? "not " : string.Empty));
        }

        if (ImGui.Checkbox(L("DisableVfx", "Disable VFX"), ref disableVfx))
        {
            _ownPermissions.SetDisableVFX(disableVfx);
        }
        _uiSharedService.DrawHelpText(L("DisableVfx.Help", "Disabling sounds will remove all VFX synced with this user on both sides.") + UiSharedService.TooltipSeparator
            + L("BidirectionalNote", "Note: this is bidirectional, either user disabling VFX sync will stop VFX sync on both sides."));
        using (ImRaii.PushIndent(indentSize, false))
        {
            _uiSharedService.BooleanToColoredIcon(!otherDisableVFX, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format(L("OtherDisableVfx", "{0} has {1}disabled VFX sync with you"), Pair.UserData.AliasOrUID, !otherDisableVFX ? "not " : string.Empty));
        }

        ImGuiHelpers.ScaledDummy(0.5f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0.5f);

        bool hasChanges = _ownPermissions != Pair.UserPair.OwnPermissions;

        using (ImRaii.Disabled(!hasChanges))
            if (_uiSharedService.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Save, L("Save", "Save")))
            {
                _ = _apiController.UserSetPairPermissions(new(Pair.UserData, _ownPermissions));
            }
        UiSharedService.AttachToolTip(L("Save.Tooltip", "Save and apply all changes"));
        
        var rightSideButtons = _uiSharedService.GetIconTextButtonSize(Dalamud.Interface.FontAwesomeIcon.Undo, L("Revert", "Revert")) +
                               _uiSharedService.GetIconTextButtonSize(Dalamud.Interface.FontAwesomeIcon.ArrowsSpin, L("Reset", "Reset to Default"));
        var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;

        ImGui.SameLine(availableWidth - rightSideButtons);

        using (ImRaii.Disabled(!hasChanges))
            if (_uiSharedService.IconTextButton(Dalamud.Interface.FontAwesomeIcon.Undo, L("Revert", "Revert")))
            {
                _ownPermissions = Pair.UserPair.OwnPermissions.DeepClone();
            }
        UiSharedService.AttachToolTip(L("Revert.Tooltip", "Revert all changes"));
        
        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(Dalamud.Interface.FontAwesomeIcon.ArrowsSpin, L("Reset", "Reset to Default")))
        {
            _ownPermissions.SetPaused(false);
            _ownPermissions.SetDisableVFX(false);
            _ownPermissions.SetDisableSounds(false);
            _ownPermissions.SetDisableAnimations(false);
            _ = _apiController.UserSetPairPermissions(new(Pair.UserData, _ownPermissions));
        }
        UiSharedService.AttachToolTip(L("Reset.Tooltip", "This will set all permissions to their default setting"));
        
        var ySize = ImGui.GetCursorPosY() + style.FramePadding.Y * ImGuiHelpers.GlobalScale + style.FrameBorderSize;
        ImGui.SetWindowSize(new(400, ySize));
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
    
    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"PermissionWindowUI.{key}", fallback);
    }
}
