using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using ElezenTools.UI;
using Snowcloak.API.Dto.User;
using System.Numerics;

namespace Snowcloak.UI.Components;

public static class CharacterProfileUiShared
{
    private static readonly Vector4 HeaderBackground = new(0.055f, 0.075f, 0.12f, 1f);
    private static readonly Vector4 HeaderAccent = new(0.18f, 0.58f, 0.82f, 1f);
    private static readonly Vector4 HeaderAccentMuted = new(0.14f, 0.32f, 0.48f, 1f);
    private static readonly Vector4 SectionAccent = new(0.45f, 0.74f, 0.94f, 1f);

    public static void DrawHeader(CharacterProfileDocumentDto document, string fallbackName, bool compact = false)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var height = compact ? 68f : 88f;
        var end = start + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(start, end, Colour.Vector4ToColour(HeaderBackground), 7f);
        drawList.AddRectFilled(start, start + new Vector2(width, 4f), Colour.Vector4ToColour(HeaderAccent), 7f);
        drawList.AddRectFilled(start + new Vector2(width - 82f, 4f), end,
            Colour.Vector4ToColour(HeaderAccentMuted), 0f);
        drawList.AddRectFilled(start + new Vector2(width - 66f, 4f), end,
            Colour.Vector4ToColour(new Vector4(HeaderBackground.X, HeaderBackground.Y, HeaderBackground.Z, 0.92f)), 0f);

        ImGui.Dummy(new Vector2(width, height));
        var afterHeader = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(start + new Vector2(14f, compact ? 13f : 16f));

        var characterName = string.IsNullOrWhiteSpace(document.CharacterName) ? fallbackName : document.CharacterName;
        ImGui.TextColored(ImGuiColors.DalamudWhite, string.IsNullOrWhiteSpace(characterName) ? "Unnamed character" : characterName);

        var subtitle = BuildSubtitle(document);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            ImGui.TextColored(SectionAccent, subtitle);
        }

        if (!compact && !string.IsNullOrWhiteSpace(document.Tagline))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, document.Tagline);
        }

        ImGui.SetCursorScreenPos(afterHeader);
    }

    public static void DrawSectionTitle(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(SectionAccent, title);
        ImGui.Separator();
    }

    public static void DrawLabelValue(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.SameLine();
        ImGui.TextWrapped(value);
    }

    public static string BuildSubtitle(CharacterProfileDocumentDto document)
    {
        var parts = new[] { document.Title, document.Pronouns, document.RpStatus }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        return string.Join("  |  ", parts);
    }
}
