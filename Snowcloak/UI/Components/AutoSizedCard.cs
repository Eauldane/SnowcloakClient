using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace Snowcloak.UI.Components;

internal static class AutoSizedCard
{
    public static void Draw(Action<float> drawContent, Vector2? padding = null)
    {
        ArgumentNullException.ThrowIfNull(drawContent);

        var scale = ImGuiHelpers.GlobalScale;
        var cardMin = ImGui.GetCursorScreenPos();
        var cardWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        var cardPadding = padding ?? new Vector2(12f, 9f) * scale;
        var innerWidth = Math.Max(1f, cardWidth - cardPadding.X * 2f);
        var drawList = ImGui.GetWindowDrawList();

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(cardMin + cardPadding);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + innerWidth);

        drawContent(innerWidth);

        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var contentMax = ImGui.GetItemRectMax();
        var cardMax = new Vector2(cardMin.X + cardWidth, contentMax.Y + cardPadding.Y);
        var rounding = 4f * scale;

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(cardMin, cardMax, Colour.Vector4ToColour(SnowcloakColours.CompactPanelAlt), rounding);
        drawList.AddRect(cardMin, cardMax, Colour.Vector4ToColour(SnowcloakColours.CompactBorderSubtle),
            rounding, ImDrawFlags.None, scale);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(cardMin.X, cardMax.Y));
    }
}
