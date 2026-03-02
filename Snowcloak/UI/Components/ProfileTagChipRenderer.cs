using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Services;
using System;
using System.Numerics;

namespace Snowcloak.UI.Components;

public static class ProfileTagChipRenderer
{
    public static int DrawTagChips(IReadOnlyList<UserProfileTagDto>? tags, string idPrefix)
    {
        if (tags == null || tags.Count == 0)
        {
            return -1;
        }

        var defaultSpacing = ImGui.GetStyle().ItemSpacing;
        using var compactSpacingStyle = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(defaultSpacing.X, 1f));
        var itemSpacingX = ImGui.GetStyle().ItemSpacing.X;
        var lineWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var lineUsedWidth = 0f;
        var clickedIndex = -1;

        for (var i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            var buttonWidth = MeasureTagWidth(tag.Type, tag.Value);
            var shouldPlaceOnSameLine = lineUsedWidth > 0f;

            if (shouldPlaceOnSameLine && lineUsedWidth + itemSpacingX + buttonWidth > lineWidth)
            {
                ImGui.NewLine();
                lineWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
                lineUsedWidth = 0f;
                shouldPlaceOnSameLine = false;
            }

            if (shouldPlaceOnSameLine)
            {
                ImGui.SameLine(0f, itemSpacingX);
                lineUsedWidth += itemSpacingX;
            }

            if (DrawTagChip(tag, tag.Value, $"{idPrefix}_{i}") && clickedIndex < 0)
            {
                clickedIndex = i;
            }

            lineUsedWidth += buttonWidth;
        }

        return clickedIndex;
    }

    public static string GetTypeLabel(ProfileTagType type)
    {
        return type switch
        {
            ProfileTagType.ChatStyle => "Chat Style",
            ProfileTagType.WritingStyle => "Writing Style",
            ProfileTagType.LikedCharacter => "Liked Characters",
            ProfileTagType.OwnCharacter => "Own Character",
            ProfileTagType.Timezone => "Timezone",
            ProfileTagType.Kink => "Kink",
            _ => "Other",
        };
    }

    private static bool DrawTagChip(UserProfileTagDto tag, string displayValue, string id)
    {
        var buttonColor = GetTagColor(tag);
        var hoverColor = AdjustBrightness(buttonColor, 1.08f);
        var activeColor = AdjustBrightness(buttonColor, 0.9f);
        var textColor = GetTextColor(buttonColor);

        using var idScope = ImRaii.PushId(id);
        using var buttonStyle = ImRaii.PushColor(ImGuiCol.Button, buttonColor);
        using var buttonHoverStyle = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverColor);
        using var buttonActiveStyle = ImRaii.PushColor(ImGuiCol.ButtonActive, activeColor);
        using var textStyle = ImRaii.PushColor(ImGuiCol.Text, textColor);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f);

        return ElezenImgui.ShowIconButton(GetTagIcon(tag.Type), displayValue);
    }

    private static FontAwesomeIcon GetTagIcon(ProfileTagType type)
    {
        return type switch
        {
            ProfileTagType.ChatStyle => FontAwesomeIcon.Comments,
            ProfileTagType.WritingStyle => FontAwesomeIcon.Pen,
            ProfileTagType.LikedCharacter => FontAwesomeIcon.Search,
            ProfileTagType.OwnCharacter => FontAwesomeIcon.User,
            ProfileTagType.Timezone => FontAwesomeIcon.Clock,
            ProfileTagType.Kink => FontAwesomeIcon.Heart,
            _ => FontAwesomeIcon.InfoCircle,
        };
    }

    private static Vector4 GetTagColor(UserProfileTagDto tag)
    {
        var hash = ComputeTagHash(tag);
        var t = (hash % 1000) / 999f;

        return tag.Type switch
        {
            ProfileTagType.ChatStyle => Hsv(0.46f + 0.08f * t, 0.72f, 0.76f),
            ProfileTagType.WritingStyle => Hsv(0.56f + 0.08f * t, 0.7f, 0.8f),
            ProfileTagType.LikedCharacter => Hsv(0.53f + 0.08f * t, 0.66f, 0.78f),
            ProfileTagType.OwnCharacter => Hsv(0.27f + 0.10f * t, 0.66f, 0.76f),
            ProfileTagType.Timezone => Hsv(0.08f + 0.06f * t, 0.78f, 0.82f),
            ProfileTagType.Kink => Hsv(0.92f + 0.06f * t, 0.7f, 0.8f),
            _ => Hsv(0.58f, 0.2f + 0.1f * t, 0.55f + 0.1f * t),
        };
    }

    private static uint ComputeTagHash(UserProfileTagDto tag)
    {
        unchecked
        {
            var hash = 2166136261u;
            var normalized = ProfileTagUtilities.NormalizeForLookup(tag.Value);
            hash = (hash ^ (uint)tag.Type) * 16777619u;
            foreach (var ch in normalized)
            {
                hash = (hash ^ ch) * 16777619u;
            }

            return hash;
        }
    }

    private static Vector4 Hsv(float h, float s, float v)
    {
        h = (h % 1f + 1f) % 1f;
        var scaledHue = h * 6f;
        var sector = (int)MathF.Floor(scaledHue);
        var fraction = scaledHue - sector;

        var p = v * (1f - s);
        var q = v * (1f - fraction * s);
        var t = v * (1f - (1f - fraction) * s);

        return (sector % 6) switch
        {
            0 => new Vector4(v, t, p, 1f),
            1 => new Vector4(q, v, p, 1f),
            2 => new Vector4(p, v, t, 1f),
            3 => new Vector4(p, q, v, 1f),
            4 => new Vector4(t, p, v, 1f),
            _ => new Vector4(v, p, q, 1f),
        };
    }

    private static Vector4 AdjustBrightness(Vector4 color, float multiplier)
    {
        return new Vector4(
            Math.Clamp(color.X * multiplier, 0f, 1f),
            Math.Clamp(color.Y * multiplier, 0f, 1f),
            Math.Clamp(color.Z * multiplier, 0f, 1f),
            color.W);
    }

    private static Vector4 GetTextColor(Vector4 color)
    {
        var luminance = color.X * 0.299f + color.Y * 0.587f + color.Z * 0.114f;
        return luminance > 0.62f ? new Vector4(0.08f, 0.08f, 0.08f, 1f) : Vector4.One;
    }

    private static float MeasureTagWidth(ProfileTagType type, string value)
    {
        var width = ElezenImgui.GetIconButtonTextSize(GetTagIcon(type), value);
        var borderWidth = ImGui.GetStyle().FrameBorderSize * 2f;
        return width + borderWidth + 1f;
    }
}
