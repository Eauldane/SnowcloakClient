using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ElezenTools.UI;

namespace Snowcloak.UI.Components;

internal static class CharaDataHubCard
{
    private static readonly Vector4 CardFill = new(0.030f, 0.075f, 0.108f, 0.62f);

    public static void Info(string text) => Notice(FontAwesomeIcon.InfoCircle, text, SnowcloakColours.OnlineBlue);

    public static void Warning(string text) => Notice(FontAwesomeIcon.ExclamationTriangle, text, Dalamud.Interface.Colors.ImGuiColors.DalamudYellow);

    public static void Error(string text) => Notice(FontAwesomeIcon.TimesCircle, text, Dalamud.Interface.Colors.ImGuiColors.DalamudRed);

    public static void Notice(FontAwesomeIcon icon, string text, Vector4 accent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;
        var padding = new Vector2(12f, 9f) * scale;
        var iconGap = 10f * scale;

        ImGui.PushFont(UiBuilder.IconFont);
        var iconStr = icon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();

        var textWidth = MathF.Max(40f * scale, width - padding.X * 2f - iconSize.X - iconGap);
        var lines = FrostbrandPanelChrome.WrapText(text, textWidth);
        var lineHeight = ImGui.GetTextLineHeight();
        var spacingY = ImGui.GetStyle().ItemSpacing.Y;
        var textHeight = lines.Count * lineHeight + MathF.Max(0, lines.Count - 1) * spacingY;
        var boxHeight = MathF.Max(iconSize.Y, textHeight) + padding.Y * 2f;

        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, boxHeight);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(CardFill), 4f * scale);
        drawList.AddRect(min, max,
            Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.45f)),
            4f * scale, ImDrawFlags.None, 1f * scale);
        drawList.AddLine(min, min with { Y = max.Y }, Colour.Vector4ToColour(accent), 2f * scale);

        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(new Vector2(min.X + padding.X, min.Y + (boxHeight - iconSize.Y) * 0.5f),
            Colour.Vector4ToColour(accent), iconStr);
        ImGui.PopFont();

        var textX = min.X + padding.X + iconSize.X + iconGap;
        var textStartY = min.Y + (boxHeight - textHeight) * 0.5f;
        for (var i = 0; i < lines.Count; i++)
            drawList.AddText(new Vector2(textX, textStartY + i * (lineHeight + spacingY)),
                Colour.Vector4ToColour(SnowcloakColours.CompactTextMuted), lines[i]);

        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
        ImGui.Dummy(new Vector2(width, 0));
    }
}
