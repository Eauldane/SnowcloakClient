using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;

namespace Snowcloak.UI;

public class PermissionWindowUI : WindowMediatorSubscriberBase
{
    public Pair Pair { get; init; }

    private readonly ApiController _apiController;
    private readonly UiFontService _fontService;
    private UserPermissions _ownPermissions;

    public PermissionWindowUI(ILogger<PermissionWindowUI> logger, Pair pair, SnowMediator mediator, UiFontService fontService,
        ApiController apiController, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, $"Permissions for {pair.UserData.AliasOrUID}" +
            "###SnowcloakSyncPermissions" + pair.UserData.UID,
            performanceCollectorService)
    {
        Pair = pair;
        _fontService = fontService;
        _apiController = apiController;
        _ownPermissions = pair.UserPair?.OwnPermissions ?? default;
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoResize;
        SetScaledSizeConstraints(new Vector2(450, 100), new Vector2(450, 500));
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

        _fontService.BigText(string.Format("Permissions for {0}", Pair.UserData.AliasOrUID));
        ImGuiHelpers.ScaledDummy(1f);

        if (Pair.UserPair == null)
            return;

        if (ImGui.Checkbox("Pause Sync", ref paused))
        {
            _ownPermissions.SetPaused(paused);
        }
        ElezenImgui.DrawHelpText("Pausing will completely cease any sync with this user." + ElezenImgui.TooltipSeparator
            +"Note: this is bidirectional, either user pausing will cease sync completely.");
        var otherPerms = Pair.UserPair.OtherPermissions;

        var otherIsPaused = otherPerms.IsPaused();
        var otherDisableSounds = otherPerms.IsDisableSounds();
        var otherDisableAnimations = otherPerms.IsDisableAnimations();
        var otherDisableVFX = otherPerms.IsDisableVFX();

        using (ImRaii.PushIndent(indentSize, false))
        {
            ElezenImgui.GetBooleanIcon(!otherIsPaused, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format("{0} has {1}paused you", Pair.UserData.AliasOrUID, !otherIsPaused ? "not " : string.Empty));
        }

        ImGuiHelpers.ScaledDummy(0.5f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0.5f);

        if (ImGui.Checkbox("Disable Sounds", ref disableSounds))
        {
            _ownPermissions.SetDisableSounds(disableSounds);
        }
        ElezenImgui.DrawHelpText("Disabling sounds will remove all sounds synced with this user on both sides." + ElezenImgui.TooltipSeparator
            + "Note: this is bidirectional, either user disabling sound sync will stop sound sync on both sides.");
        using (ImRaii.PushIndent(indentSize, false))
        {
            ElezenImgui.GetBooleanIcon(!otherDisableSounds, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format("{0} has {1}disabled sound sync with you", Pair.UserData.AliasOrUID, !otherDisableSounds ? "not " : string.Empty));
        }

        if (ImGui.Checkbox("Disable Animations", ref disableAnimations))
        {
            _ownPermissions.SetDisableAnimations(disableAnimations);
        }
        ElezenImgui.DrawHelpText("Disabling animationss will remove all animations synced with this user on both sides." + ElezenImgui.TooltipSeparator
            + "Note: this is bidirectional, either user disabling animation sync will stop animation sync on both sides.");
        using (ImRaii.PushIndent(indentSize, false))
        {
            ElezenImgui.GetBooleanIcon(!otherDisableAnimations, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format("{0} has {1}disabled animation sync with you", Pair.UserData.AliasOrUID, !otherDisableAnimations ? "not " : string.Empty));
        }

        if (ImGui.Checkbox("Disable VFX", ref disableVfx))
        {
            _ownPermissions.SetDisableVFX(disableVfx);
        }
        ElezenImgui.DrawHelpText("Disabling sounds will remove all VFX synced with this user on both sides." + ElezenImgui.TooltipSeparator
            + "Note: this is bidirectional, either user disabling VFX sync will stop VFX sync on both sides.");
        using (ImRaii.PushIndent(indentSize, false))
        {
            ElezenImgui.GetBooleanIcon(!otherDisableVFX, false);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(string.Format("{0} has {1}disabled VFX sync with you", Pair.UserData.AliasOrUID, !otherDisableVFX ? "not " : string.Empty));
        }

        ImGuiHelpers.ScaledDummy(0.5f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(0.5f);

        bool hasChanges = _ownPermissions != Pair.UserPair.OwnPermissions;

        using (ImRaii.Disabled(!hasChanges))
            if (ElezenImgui.ShowIconButton(Dalamud.Interface.FontAwesomeIcon.Save, "Save"))
            {
                _ = _apiController.UserSetPairPermissions(new(Pair.UserData, _ownPermissions));
            }
        ElezenImgui.AttachTooltip("Save and apply all changes");
        
        var rightSideButtons = ElezenImgui.GetIconButtonTextSize(Dalamud.Interface.FontAwesomeIcon.Undo, "Revert") +
                               ElezenImgui.GetIconButtonTextSize(Dalamud.Interface.FontAwesomeIcon.ArrowsSpin, "Reset to Default");
        var availableWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;

        ImGui.SameLine(availableWidth - rightSideButtons);

        using (ImRaii.Disabled(!hasChanges))
            if (ElezenImgui.ShowIconButton(Dalamud.Interface.FontAwesomeIcon.Undo, "Revert"))
            {
                _ownPermissions = Pair.UserPair.OwnPermissions;
            }
        ElezenImgui.AttachTooltip("Revert all changes");
        
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(Dalamud.Interface.FontAwesomeIcon.ArrowsSpin, "Reset to Default"))
        {
            _ownPermissions.SetPaused(false);
            _ownPermissions.SetDisableVFX(false);
            _ownPermissions.SetDisableSounds(false);
            _ownPermissions.SetDisableAnimations(false);
            _ = _apiController.UserSetPairPermissions(new(Pair.UserData, _ownPermissions));
        }
        ElezenImgui.AttachTooltip("This will set all permissions to their default setting");
        
        var ySize = ImGui.GetCursorPosY() + style.FramePadding.Y * ImGuiHelpers.GlobalScale + style.FrameBorderSize;
        ImGui.SetWindowSize(new(400, ySize));
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}
