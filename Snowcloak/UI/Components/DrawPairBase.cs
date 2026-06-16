using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ElezenTools.UI;
using Snowcloak.API.Data.Extensions;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.UI.Handlers;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI.Components;

public abstract class DrawPairBase
{
    private readonly ApiController _apiController;
    private readonly UidDisplayHandler _displayHandler;
    private readonly string _id;
    private readonly PairRowTextState _textState = new();

    protected DrawPairBase(string id, Pair entry, ApiController apiController, UidDisplayHandler uIDDisplayHandler)
    {
        _id = id;
        PairEntry = entry;
        _apiController = apiController;
        _displayHandler = uIDDisplayHandler;
    }

    public string ImGuiID => _id;
    public string UID => PairEntry.UserData.UID;
    public Pair Pair => PairEntry;
    protected ApiController ApiController => _apiController;
    protected UidDisplayHandler DisplayHandler => _displayHandler;
    protected Pair PairEntry { get; set; }

    protected abstract void DrawLeftSide(float textPosY, float originalY);

    protected abstract float DrawRightSide(float textPosY, float originalY);

    protected virtual void DrawAfterName(float originalY, float textEndX, float rightSide)
    {
    }

    internal static Vector2 RowActionButtonSize => new(30f * ImGuiHelpers.GlobalScale, 30f * ImGuiHelpers.GlobalScale);

    internal static bool DrawRowActionButton(FontAwesomeIcon icon, string id, Vector4? iconColor = null)
    {
        var size = RowActionButtonSize;
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##row-action-{id}", size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var center = min + size * 0.5f;
        var drawList = ImGui.GetWindowDrawList();

        if (hovered || active)
        {
            var fill = active
                ? new Vector4(0.180f, 0.330f, 0.500f, 0.90f)
                : new Vector4(0.100f, 0.170f, 0.245f, 0.78f);
            drawList.AddCircleFilled(center, size.X * 0.43f, ElezenTools.UI.Colour.Vector4ToColour(fill), 24);
        }

        var iconText = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText);
        drawList.AddText(min + (size - iconSize) * 0.5f, ElezenTools.UI.Colour.Vector4ToColour(iconColor ?? Vector4.One), iconText);
        ImGui.PopFont();

        return clicked;
    }

    /// <summary>
    /// Appends the shared "Mods Info" block (file size / VRAM / triangle count) to a presence
    /// tooltip. Shared by direct-pair and syncshell-member rows so the wording stays in sync.
    /// </summary>
    protected string AppendModsInfoTooltip(string tooltip)
    {
        if (PairEntry.LastAppliedDataBytes < 0)
        {
            return tooltip;
        }

        tooltip += ElezenImgui.TooltipSeparator;
        tooltip += ((!PairEntry.IsVisible) ? "(Last) " : string.Empty) + "Mods Info" + Environment.NewLine;
        tooltip += "Files Size: " + ElezenImgui.ByteToString(PairEntry.LastAppliedDataBytes, true);
        if (PairEntry.LastAppliedApproximateVRAMBytes >= 0)
        {
            tooltip += Environment.NewLine + "Approx. VRAM Usage: " + ElezenImgui.ByteToString(PairEntry.LastAppliedApproximateVRAMBytes, true);
        }
        if (PairEntry.LastAppliedDataTris >= 0)
        {
            tooltip += Environment.NewLine + "Triangle Count (excl. Vanilla): "
                + (PairEntry.LastAppliedDataTris > 1000 ? (PairEntry.LastAppliedDataTris / 1000d).ToString("0.0'k'", CultureInfo.CurrentCulture) : PairEntry.LastAppliedDataTris);
        }

        return tooltip;
    }

    /// <summary>
    /// Draws the body of the "Individual User permissions" tooltip (sound/animation/VFX state for
    /// the direct pair). Caller wraps this in its own Begin/EndTooltip. Shared by both row types.
    /// </summary>
    protected void DrawIndividualPermissionsTooltipBody(bool soundsDisabled, bool animDisabled, bool vfxDisabled)
    {
        ImGui.TextUnformatted("Individual User permissions");

        if (soundsDisabled)
        {
            ElezenImgui.ShowIcon(FontAwesomeIcon.VolumeOff);
            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "Sound sync disabled with {0}", PairEntry.UserData.AliasOrUID));
            ImGui.NewLine();
            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "You: {0}, They: {1}", PairEntry.UserPair!.OwnPermissions.IsDisableSounds() ? "Disabled" : "Enabled", PairEntry.UserPair!.OtherPermissions.IsDisableSounds() ? "Disabled" : "Enabled"));
        }

        if (animDisabled)
        {
            ElezenImgui.ShowIcon(FontAwesomeIcon.Stop);
            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "Animation sync disabled with {0}", PairEntry.UserData.AliasOrUID));
            ImGui.NewLine();
            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "You: {0}, They: {1}", PairEntry.UserPair!.OwnPermissions.IsDisableAnimations() ? "Disabled" : "Enabled", PairEntry.UserPair!.OtherPermissions.IsDisableAnimations() ? "Disabled" : "Enabled"));
        }

        if (vfxDisabled)
        {
            ElezenImgui.ShowIcon(FontAwesomeIcon.Circle);
            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "VFX sync disabled with {0}", PairEntry.UserData.AliasOrUID));
            ImGui.NewLine();
            ImGui.SameLine(40 * ImGuiHelpers.GlobalScale);
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "You: {0}, They: {1}", PairEntry.UserPair!.OwnPermissions.IsDisableVFX() ? "Disabled" : "Enabled", PairEntry.UserPair!.OtherPermissions.IsDisableVFX() ? "Disabled" : "Enabled"));
        }
    }

    private float DrawName(float textPosY, float leftSide, float rightSide)
    {
        return _displayHandler.DrawPairText(_id, PairEntry, leftSide, textPosY, () => rightSide - leftSide, _textState);
    }

    public void DrawPairedClient()
    {
        var originalY = ImGui.GetCursorPosY();
        var textSize = ImGui.CalcTextSize(PairEntry.UserData.AliasOrUID);
        ImGui.PushFont(UiBuilder.IconFont);
        var statusIconSize = ImGui.CalcTextSize(FontAwesomeIcon.Snowflake.ToIconString());
        ImGui.PopFont();

        var startPos = ImGui.GetCursorStartPos();

        var framePadding = ImGui.GetStyle().FramePadding;
        var lineHeight = Math.Max(textSize.Y + framePadding.Y * 2, RowActionButtonSize.Y + 2f * ImGuiHelpers.GlobalScale);

        var off = startPos.Y;
        var height = ElezenImgui.GetWindowContentRegionHeight();

        if ((originalY + off) < -lineHeight || (originalY + off) > height)
        {
            ImGui.Dummy(new System.Numerics.Vector2(0f, lineHeight));
            return;
        }

        var rowMin = ImGui.GetCursorScreenPos();
        var rowMax = rowMin + new Vector2(ImGui.GetContentRegionAvail().X, lineHeight);
        var drawList = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(rowMin, rowMax);
        var rowFill = hovered
            ? new Vector4(0.075f, 0.130f, 0.185f, 0.42f)
            : new Vector4(0.030f, 0.070f, 0.100f, 0.055f);
        drawList.AddRectFilled(rowMin, rowMax, ElezenTools.UI.Colour.Vector4ToColour(rowFill), 0f);
        drawList.AddLine(rowMin with { Y = rowMax.Y }, rowMax, ElezenTools.UI.Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.16f)), 1f * ImGuiHelpers.GlobalScale);

        var textPosY = originalY + (lineHeight - textSize.Y) * 0.5f;
        var iconPosY = originalY + (lineHeight - statusIconSize.Y) * 0.5f;
        var controlPosY = originalY + Math.Max(0f, (lineHeight - RowActionButtonSize.Y) * 0.5f);
        DrawLeftSide(iconPosY, originalY);
        ImGui.SameLine();
        var posX = ImGui.GetCursorPosX();
        var rightSide = DrawRightSide(textPosY, controlPosY);
        var textEndX = DrawName(textPosY, posX, rightSide);
        DrawAfterName(originalY, textEndX, rightSide);
    }

    internal sealed class PairRowTextState
    {
        public bool ShowUidInsteadOfName { get; set; }
        public bool EditingNote { get; set; }
        public string EditUserComment { get; set; } = string.Empty;
        public string LastMouseOverUid { get; set; } = string.Empty;
        public bool PopupShown { get; set; }
        public DateTime? PopupTime { get; set; }
    }
}
