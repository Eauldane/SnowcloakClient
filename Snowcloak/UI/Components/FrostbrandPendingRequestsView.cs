using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using ElezenTools.UI.Mvu;
using Snowcloak.Services.Pairing;

namespace Snowcloak.UI.Components;

public static class FrostbrandPendingRequestsView
{
    public static void Draw(AvailabilityViewState state, IDispatcher dispatcher, string localisationPrefix)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(dispatcher);

        FrostbrandPanelChrome.DrawSectionTitle(FontAwesomeIcon.UserPlus, "Pending pair requests");
        if (state.PendingRequestCount == 0)
        {
            DrawPendingEmptyState();
            return;
        }

        PendingPairRequestSection.Draw(state.PendingRequests, dispatcher, tagHandler: null,
            localisationPrefix: localisationPrefix, indent: false);
    }

    private static void DrawPendingEmptyState()
    {
        var fullWidth = ImGui.GetContentRegionAvail().X;
        var startX = ImGui.GetCursorPosX();
        var textWidth = fullWidth * 0.78f;

        ImGuiHelpers.ScaledDummy(new Vector2(0, 22));

        DrawCenteredBigIcon(FontAwesomeIcon.UserPlus, SnowcloakColours.CompactTextMuted, 2.0f);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 12));

        DrawCenteredLine("No pending pair requests right now.", Vector4.One, startX, fullWidth);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 6));

        DrawCenteredWrappedText("Pair requests appear here when a nearby Frostbrand user sends you a request while you're opted in.",
            SnowcloakColours.CompactTextMuted, startX, fullWidth, textWidth);

        ImGuiHelpers.ScaledDummy(new Vector2(0, 12));
        DrawInsetSeparator(fullWidth, 0.12f);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 12));

        DrawCenteredWrappedText("Need to fine-tune who can send a request? Head to the settings tab and set some filters.",
            SnowcloakColours.CompactTextMuted, startX, fullWidth, textWidth);
    }

    private static void DrawCenteredBigIcon(FontAwesomeIcon icon, Vector4 color, float fontScale)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        ImGui.SetWindowFontScale(fontScale);
        var iconStr = icon.ToIconString();
        var size = ImGui.CalcTextSize(iconStr);
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (avail - size.X) * 0.5f));
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(iconStr);
        ImGui.SetWindowFontScale(1f);
    }

    private static void DrawCenteredLine(string text, Vector4 color, float startX, float fullWidth)
    {
        var size = ImGui.CalcTextSize(text);
        ImGui.SetCursorPosX(startX + MathF.Max(0f, (fullWidth - size.X) * 0.5f));
        ImGui.TextColored(color, text);
    }

    private static void DrawCenteredWrappedText(string text, Vector4 color, float startX, float fullWidth, float maxWidth)
    {
        foreach (var line in FrostbrandPanelChrome.WrapText(text, maxWidth))
        {
            var size = ImGui.CalcTextSize(line);
            ImGui.SetCursorPosX(startX + MathF.Max(0f, (fullWidth - size.X) * 0.5f));
            ImGui.TextColored(color, line);
        }
    }

    private static void DrawInsetSeparator(float fullWidth, float marginFraction)
    {
        var margin = fullWidth * marginFraction;
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(new Vector2(p.X + margin, p.Y), new Vector2(p.X + fullWidth - margin, p.Y),
            Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.40f)), 1f * ImGuiHelpers.GlobalScale);
        ImGui.Dummy(new Vector2(fullWidth, 1f * ImGuiHelpers.GlobalScale));
    }
}
