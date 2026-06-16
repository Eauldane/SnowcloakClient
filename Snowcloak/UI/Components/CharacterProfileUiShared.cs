using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Dto.User;
using Snowcloak.PlayerData.Moodles;
using Snowcloak.UI;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI.Components;

public static class CharacterProfileUiShared
{
    private static readonly Vector4 HeaderBackground = new(0.055f, 0.075f, 0.12f, 1f);
    private static readonly Vector4 DefaultHeaderAccent = new(0.18f, 0.58f, 0.82f, 1f);
    private static readonly Vector4 SectionAccent = new(0.45f, 0.74f, 0.94f, 1f);
    private static readonly Vector4 BadgeText = new(0.93f, 0.95f, 0.98f, 1f);

    public static void DrawHeader(CharacterProfileDocumentDto document, string fallbackName, bool compact = false,
        IDalamudTextureWrap? headerImageTexture = null)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var height = compact ? 78f : 132f;
        var end = start + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        var accent = ParseAccentColor(document.HeaderAccentColorHex);

        drawList.AddRectFilled(start, end, Colour.Vector4ToColour(HeaderBackground), 8f);
        if (headerImageTexture != null)
        {
            drawList.AddImage(headerImageTexture.Handle, start, end);
            drawList.AddRectFilled(start, end, Colour.Vector4ToColour(new Vector4(0f, 0f, 0f, 0.34f)), 8f);
        }
        else
        {
            drawList.AddRectFilled(start, end, Colour.Vector4ToColour(new Vector4(accent.X * 0.12f, accent.Y * 0.12f, accent.Z * 0.12f, 1f)), 8f);
            drawList.AddRectFilled(start + new Vector2(width - 140f, 4f), end,
                Colour.Vector4ToColour(new Vector4(accent.X * 0.45f, accent.Y * 0.45f, accent.Z * 0.45f, 0.52f)), 0f);
        }
        drawList.AddRectFilled(start, start + new Vector2(width, 5f), Colour.Vector4ToColour(accent), 8f);

        ImGui.Dummy(new Vector2(width, height));
        var afterHeader = ImGui.GetCursorScreenPos();

        var characterName = ResolveHeaderName(document.CharacterName, fallbackName);
        var subtitle = BuildSubtitle(document);
        var titleScale = compact ? 1.08f : 1.34f;
        var baseFontSize = ImGui.GetFontSize();
        var lineSpacing = compact ? 2f : 4f;
        var verticalPadding = compact ? 8f : 10f;
        var titleSize = ImGui.CalcTextSize(characterName) * titleScale;
        var subtitleSize = string.IsNullOrWhiteSpace(subtitle) ? Vector2.Zero : ImGui.CalcTextSize(subtitle);
        var taglineSize = !compact && !string.IsNullOrWhiteSpace(document.Tagline) ? ImGui.CalcTextSize(document.Tagline) : Vector2.Zero;
        var maxPanelWidth = MathF.Max(180f, width - 24f);
        var panelWidth = MathF.Min(maxPanelWidth, MathF.Max(titleSize.X, MathF.Max(subtitleSize.X, taglineSize.X)) + 28f);
        var contentHeight = baseFontSize * titleScale;
        if (!string.IsNullOrWhiteSpace(subtitle))
            contentHeight += lineSpacing + baseFontSize;
        if (!compact && !string.IsNullOrWhiteSpace(document.Tagline))
            contentHeight += lineSpacing + baseFontSize;
        var panelHeight = MathF.Max(compact ? 48f : 62f, contentHeight + verticalPadding * 2f);
        var panelStart = start + new Vector2(12f, compact ? 14f : 24f);
        var panelEnd = panelStart + new Vector2(MathF.Min(maxPanelWidth, MathF.Max(240f, panelWidth)), panelHeight);

        DrawFadedTextPatch(drawList, panelStart, panelEnd);
        var textStart = panelStart + new Vector2(14f, verticalPadding);
        var currentY = textStart.Y;
        ImGui.SetCursorScreenPos(new Vector2(textStart.X, currentY));
        using (ElezenFonts.Push(baseFontSize * titleScale))
        {
            ImGui.TextColored(ImGuiColors.DalamudWhite, characterName);
        }
        currentY += baseFontSize * titleScale + lineSpacing;

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            ImGui.SetCursorScreenPos(new Vector2(textStart.X, currentY));
            ImGui.TextColored(accent, subtitle);
            currentY += baseFontSize + lineSpacing;
        }
        if (!compact && !string.IsNullOrWhiteSpace(document.Tagline))
        {
            ImGui.SetCursorScreenPos(new Vector2(textStart.X, currentY));
            ImGui.TextColored(ImGuiColors.DalamudGrey, document.Tagline);
        }

        ImGui.SetCursorScreenPos(afterHeader);
    }

    public static void DrawSectionTitle(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(SectionAccent, title);
        var min = ImGui.GetCursorScreenPos();
        var max = min with { X = min.X + MathF.Max(1f, ImGui.GetContentRegionAvail().X) };
        ImGui.GetWindowDrawList().AddLine(min, max,
            Colour.Vector4ToColour(new Vector4(SectionAccent.X, SectionAccent.Y, SectionAccent.Z, 0.28f)),
            1f * ImGuiHelpers.GlobalScale);
        ImGui.Dummy(new Vector2(0f, 4f * ImGuiHelpers.GlobalScale));
    }

    public static void DrawLabelValue(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.SameLine();
        ImGui.TextWrapped(value);
    }

    public static void DrawProfileBadges(CharacterProfileDocumentDto document, string idPrefix)
    {
        var badges = BuildProfileBadges(document).ToList();
        if (badges.Count == 0)
            return;

        var accent = ParseAccentColor(document.HeaderAccentColorHex);
        var mutedAccent = new Vector4(accent.X * 0.45f, accent.Y * 0.45f, accent.Z * 0.45f, 0.92f);
        var spacing = ImGui.GetStyle().ItemSpacing;
        var lineWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var usedWidth = 0f;

        using var compactSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 3f));
        for (var i = 0; i < badges.Count; i++)
        {
            var badge = badges[i];
            var width = ImGui.CalcTextSize(badge).X + ImGui.GetStyle().FramePadding.X * 2f + 2f;
            var sameLine = usedWidth > 0f;
            if (sameLine && usedWidth + spacing.X + width > lineWidth)
            {
                ImGui.NewLine();
                usedWidth = 0f;
                sameLine = false;
            }

            if (sameLine)
            {
                ImGui.SameLine(0f, spacing.X);
                usedWidth += spacing.X;
            }

            DrawBadge($"{idPrefix}-{i}", badge, i == 0 ? accent : mutedAccent);
            usedWidth += width;
        }
    }

    public static void DrawMoodles(string? moodlesData, string idPrefix, TextureService textureService, int maxVisible = 12)
    {
        if (!MoodlesDataParser.TryParse(moodlesData, out var parsedStatuses))
            return;

        var statuses = parsedStatuses
            .Where(IsRenderableMoodle)
            .Take(maxVisible)
            .ToList();
        if (statuses.Count == 0)
            return;

        DrawSectionTitle("Moodles");
        DrawMoodleIcons(statuses, idPrefix, textureService);

        var renderableCount = parsedStatuses.Count(IsRenderableMoodle);
        if (renderableCount > statuses.Count)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"+{renderableCount - statuses.Count} more Moodle status{(renderableCount - statuses.Count == 1 ? string.Empty : "es")}");
        }
    }

    public static string BuildSubtitle(CharacterProfileDocumentDto document)
    {
        var parts = new[] { document.Title, document.Pronouns }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        return string.Join("  |  ", parts);
    }

    private static IEnumerable<string> BuildProfileBadges(CharacterProfileDocumentDto document)
    {
        if (!string.IsNullOrWhiteSpace(document.RpStatus))
            yield return FormatStatusBadge(document.RpStatus);
        if (!string.IsNullOrWhiteSpace(document.Approachability))
            yield return $"Approach: {document.Approachability.Trim()}";
    }

    private static string FormatStatusBadge(string value)
    {
        var trimmed = value.Trim();
        var normalized = trimmed.ToLowerInvariant();
        if (normalized.Contains("ooc", StringComparison.Ordinal)
            || normalized.Contains("out of character", StringComparison.Ordinal))
            return $"OOC: {trimmed}";
        if (normalized.Contains("ic", StringComparison.Ordinal)
            || normalized.Contains("in character", StringComparison.Ordinal))
            return $"IC: {trimmed}";
        return $"RP: {trimmed}";
    }

    private static string ResolveHeaderName(string? characterName, string fallbackName)
    {
        var resolved = string.IsNullOrWhiteSpace(characterName) ? fallbackName : characterName;
        return IsPlaceholderHeaderName(resolved) ? "Unnamed character" : resolved;
    }

    private static bool IsPlaceholderHeaderName(string? value)
        => string.IsNullOrWhiteSpace(value)
           || string.Equals(value, "Loading RP profile...", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "Loading RP Profile", StringComparison.OrdinalIgnoreCase);

    private static void DrawMoodleIcons(IReadOnlyList<MoodlesStatusData> statuses, string idPrefix, TextureService textureService)
    {
        var iconSize = 48f * ImGuiHelpers.GlobalScale;
        var spacing = 2f * ImGuiHelpers.GlobalScale;
        var available = MathF.Max(iconSize, ImGui.GetContentRegionAvail().X);
        var columns = Math.Max(1, (int)Math.Floor((available + spacing) / (iconSize + spacing)));
        var columnIndex = 0;

        for (var i = 0; i < statuses.Count; i++)
        {
            DrawMoodleIcon(statuses[i], iconSize, textureService, $"{idPrefix}-moodle-{i}");
            columnIndex++;
            if (i < statuses.Count - 1 && columnIndex < columns)
            {
                ImGui.SameLine(0f, spacing);
            }
            else
            {
                columnIndex = 0;
            }
        }
    }

    private static void DrawBadge(string id, string label, Vector4 color)
    {
        using var idScope = ImRaii.PushId(id);
        using var buttonStyle = ImRaii.PushColor(ImGuiCol.Button, color);
        using var buttonHoverStyle = ImRaii.PushColor(ImGuiCol.ButtonHovered, AdjustBrightness(color, 1.08f));
        using var buttonActiveStyle = ImRaii.PushColor(ImGuiCol.ButtonActive, AdjustBrightness(color, 0.92f));
        using var textStyle = ImRaii.PushColor(ImGuiCol.Text, BadgeText);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.Button(label);
    }

    private static string StripBasicBbCode(string value)
    {
        var result = value;
        while (true)
        {
            var start = result.IndexOf('[', StringComparison.Ordinal);
            if (start < 0)
                return result;

            var end = result.IndexOf(']', start);
            if (end <= start)
                return result;

            result = result.Remove(start, end - start + 1);
        }
    }

    private static void DrawMoodleIcon(MoodlesStatusData moodle, float iconSize, TextureService textureService, string id)
    {
        var iconId = moodle.IconID;
        if (moodle.Stacks > 1)
        {
            iconId += moodle.Stacks - 1;
        }

        using var idScope = ImRaii.PushId(id);
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(iconSize, iconSize));

        if (iconId > 0 && textureService.TryGetGameIcon((uint)iconId, out var icon))
        {
            var wrap = icon!.GetWrapOrEmpty();
            var size = GetScaledIconSize(wrap, iconSize);
            var offset = new Vector2((iconSize - size.X) / 2f, (iconSize - size.Y) / 2f);
            ImGui.GetWindowDrawList().AddImage(wrap.Handle, cursor + offset, cursor + offset + size);
        }

        if (ImGui.IsItemHovered())
        {
            ShowMoodleTooltip(moodle);
        }
    }

    private static Vector2 GetScaledIconSize(IDalamudTextureWrap wrap, float maxSize)
    {
        var width = Math.Max(1f, wrap.Width);
        var height = Math.Max(1f, wrap.Height);
        var scale = maxSize / Math.Max(width, height);
        return new Vector2(width * scale, height * scale);
    }

    private static void ShowMoodleTooltip(MoodlesStatusData moodle)
    {
        var title = string.IsNullOrWhiteSpace(moodle.Title) ? "Unnamed Moodle" : StripBasicBbCode(moodle.Title).Trim();
        if (moodle.Stacks > 1)
        {
            title = $"{title} x{moodle.Stacks}";
        }

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(title);
        if (!string.IsNullOrWhiteSpace(moodle.Description))
        {
            ImGui.Separator();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
            ImGui.TextUnformatted(StripBasicBbCode(moodle.Description).Trim());
            ImGui.PopTextWrapPos();
        }
        ImGui.EndTooltip();
    }

    private static bool IsRenderableMoodle(MoodlesStatusData status)
        => status.IconID > 0
           || !string.IsNullOrWhiteSpace(status.Title)
           || !string.IsNullOrWhiteSpace(status.Description);

    private static Vector4 AdjustBrightness(Vector4 color, float multiplier)
    {
        return new Vector4(
            Math.Clamp(color.X * multiplier, 0f, 1f),
            Math.Clamp(color.Y * multiplier, 0f, 1f),
            Math.Clamp(color.Z * multiplier, 0f, 1f),
            color.W);
    }

    public static Vector4 ParseAccentColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultHeaderAccent;

        var trimmed = value.Trim().TrimStart('#');
        if (trimmed.Length != 6 || !uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color))
            return DefaultHeaderAccent;

        var r = ((color >> 16) & 0xFF) / 255f;
        var g = ((color >> 8) & 0xFF) / 255f;
        var b = (color & 0xFF) / 255f;
        return new Vector4(r, g, b, 1f);
    }

    public static string ToAccentColorHex(Vector3 color)
    {
        var r = (int)Math.Clamp(MathF.Round(color.X * 255f), 0f, 255f);
        var g = (int)Math.Clamp(MathF.Round(color.Y * 255f), 0f, 255f);
        var b = (int)Math.Clamp(MathF.Round(color.Z * 255f), 0f, 255f);
        return string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
    }

    private static void DrawFadedTextPatch(ImDrawListPtr drawList, Vector2 min, Vector2 max)
    {
        for (var layer = 6; layer >= 0; layer--)
        {
            var expansion = layer * 4f;
            var alpha = 0.07f + (6 - layer) * 0.085f;
            drawList.AddRectFilled(
                min - new Vector2(expansion, expansion),
                max + new Vector2(expansion, expansion),
                Colour.Vector4ToColour(new Vector4(0f, 0f, 0f, alpha)),
                18f + layer * 2f);
        }
    }
}
