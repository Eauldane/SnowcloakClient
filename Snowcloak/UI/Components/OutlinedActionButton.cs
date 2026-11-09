using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ElezenTools.UI;

namespace Snowcloak.UI.Components;

internal static class OutlinedActionButton
{
    public static float Height => 38f * ImGuiHelpers.GlobalScale;

    public static bool Draw(FontAwesomeIcon icon, string label, Vector4 color, float width)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(width, Height);
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##outlined-action-{label}", size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        var fill = hovered
            ? new Vector4(0.080f, 0.145f, 0.220f, 0.86f)
            : new Vector4(0.050f, 0.100f, 0.160f, 0.68f);
        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(fill), 3f * scale);
        drawList.AddRect(min, max, Colour.Vector4ToColour(new Vector4(color.X, color.Y, color.Z, hovered ? 0.95f : 0.72f)), 3f * scale, ImDrawFlags.None, 1f * scale);

        var iconText = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText);
        ImGui.PopFont();
        var textSize = ImGui.CalcTextSize(label);
        var contentWidth = iconSize.X + 10f * scale + textSize.X;
        var iconPos = min + new Vector2((size.X - contentWidth) * 0.5f, 0f);
        iconPos.Y = min.Y + (size.Y - iconSize.Y) * 0.5f;
        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(iconPos, Colour.Vector4ToColour(color), iconText);
        ImGui.PopFont();
        drawList.AddText(new Vector2(iconPos.X + iconSize.X + 10f * scale, min.Y + (size.Y - textSize.Y) * 0.5f), Colour.Vector4ToColour(color), label);

        return clicked;
    }
}
