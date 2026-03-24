using Dalamud.Interface;
using Snowcloak.API.Data;
using System.Numerics;

namespace Snowcloak.UI.Components;

public static class SyncshellMemberLabelUi
{
    public static string FormatLabels(IReadOnlyList<string>? labels)
    {
        if (labels == null || labels.Count == 0)
        {
            return string.Empty;
        }

        return GroupMemberLabelValidator.TryNormalizeLabels(labels, out var normalizedLabels, out _)
            ? string.Join(", ", normalizedLabels)
            : string.Join(", ", labels);
    }

    public static bool IsLabelSelected(IReadOnlyList<string>? labels, string labelValue)
    {
        if (labels == null || labels.Count == 0)
        {
            return false;
        }

        var key = GroupMemberLabelValidator.ToLookupKey(labelValue);
        return labels.Any(label => string.Equals(GroupMemberLabelValidator.ToLookupKey(label), key, StringComparison.Ordinal));
    }

    public static List<string> NormalizeSingleSelection(IReadOnlyList<string>? labels)
    {
        if (labels == null || labels.Count == 0)
        {
            return [];
        }

        GroupMemberLabelDefinition? selectedDefinition = null;
        foreach (var label in labels)
        {
            if (!GroupMemberLabelValidator.TryGetDefinition(label, out var definition))
            {
                continue;
            }

            if (selectedDefinition == null || definition.Order < selectedDefinition.Value.Order)
            {
                selectedDefinition = definition;
            }
        }

        return selectedDefinition == null ? [] : [selectedDefinition.Value.DisplayName];
    }

    public static bool TrySetLabelSelected(IReadOnlyList<string> existingLabels, string labelValue, bool isSelected, out List<string> updatedLabels, out string? errorMessage)
    {
        var labelKey = GroupMemberLabelValidator.ToLookupKey(labelValue);
        var candidateLabels = existingLabels
            .Where(label => !string.Equals(GroupMemberLabelValidator.ToLookupKey(label), labelKey, StringComparison.Ordinal))
            .ToList();

        if (isSelected)
        {
            candidateLabels.Add(labelValue);
        }

        return GroupMemberLabelValidator.TryNormalizeLabels(candidateLabels, out updatedLabels, out errorMessage);
    }

    public static bool TrySetExclusiveLabelSelected(string labelValue, bool isSelected, out List<string> updatedLabels, out string? errorMessage)
    {
        List<string> candidateLabels = isSelected ? [labelValue] : [];
        return GroupMemberLabelValidator.TryNormalizeLabels(candidateLabels, out updatedLabels, out errorMessage);
    }

    public static bool TryGetPresenceOverride(IReadOnlyList<string>? labels, out FontAwesomeIcon icon, out Vector4 color, out string tooltip)
    {
        icon = FontAwesomeIcon.None;
        color = Vector4.One;
        tooltip = string.Empty;

        if (labels == null || labels.Count == 0)
        {
            return false;
        }

        LabelStyle? selectedStyle = null;
        foreach (var label in labels)
        {
            if (!TryGetLabelPresentation(label, out var candidateIcon, out var candidateColor, out _, out var candidatePriority))
            {
                continue;
            }

            var candidateStyle = new LabelStyle(candidateIcon, candidateColor, candidatePriority);
            if (selectedStyle == null || candidateStyle.Priority < selectedStyle.Value.Priority)
            {
                selectedStyle = candidateStyle;
            }
        }

        if (selectedStyle == null)
        {
            return false;
        }

        icon = selectedStyle.Value.Icon;
        color = selectedStyle.Value.Color;
        tooltip = FormatLabels(labels);
        return true;
    }

    public static bool TryGetLabelPresentation(string label, out FontAwesomeIcon icon, out Vector4 color, out string displayName, out int priority)
    {
        icon = FontAwesomeIcon.None;
        color = Vector4.One;
        displayName = label;
        priority = int.MaxValue;

        if (!GroupMemberLabelValidator.TryGetDefinition(label, out var definition))
        {
            return false;
        }

        displayName = definition.DisplayName;
        priority = definition.Order;
        switch (definition.Value)
        {
            case "staff":
                icon = FontAwesomeIcon.IdBadge;
                color = new Vector4(0.16f, 0.44f, 0.72f, 1f);
                return true;
            case "dj":
                icon = FontAwesomeIcon.Music;
                color = new Vector4(0.58f, 0.25f, 0.76f, 1f);
                return true;
            case "bartender":
                icon = FontAwesomeIcon.Cocktail;
                color = new Vector4(0.72f, 0.5f, 0.18f, 1f);
                return true;
            case "performer":
                icon = FontAwesomeIcon.Microphone;
                color = new Vector4(0.83f, 0.46f, 0.12f, 1f);
                return true;
            case "dancer":
                icon = FontAwesomeIcon.Star;
                color = new Vector4(0.9f, 0.36f, 0.6f, 1f);
                return true;
            case "courtesan":
                icon = FontAwesomeIcon.Heart;
                color = new Vector4(0.78f, 0.2f, 0.36f, 1f);
                return true;
            case "photographer":
                icon = FontAwesomeIcon.Camera;
                color = new Vector4(0.18f, 0.58f, 0.49f, 1f);
                return true;
            default:
                return false;
        }
    }

    private readonly record struct LabelStyle(FontAwesomeIcon Icon, Vector4 Color, int Priority);
}
