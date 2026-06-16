using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using System.Numerics;

namespace Snowcloak.UI.Components;

internal static class SettingsUiControls
{
    public static void DrawCombo<T>(string comboName, IEnumerable<T> comboItems, Func<T, string> toName,
        Dictionary<string, object> selectedComboItems, Action<T?>? onSelected = null, T? initialSelectedItem = default)
    {
        var items = comboItems as IReadOnlyList<T> ?? comboItems.ToArray();
        _ = ElezenImgui.DrawCombo(comboName, items, toName, selectedComboItems, onSelected, initialSelectedItem);
    }

    public static bool InputDtrColors(string label, ref ElezenStrings.Colour colors)
    {
        using var id = ImRaii.PushId(label);
        var innerSpacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var foregroundColor = ConvertColor(colors.Foreground);
        var glowColor = ConvertColor(colors.Glow);

        var changed = ImGui.ColorEdit3("###foreground", ref foregroundColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Foreground Color - Set to pure black (#000000) to use the default color");
        }

        ImGui.SameLine(0.0f, innerSpacing);
        changed |= ImGui.ColorEdit3("###glow", ref glowColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.Uint8);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Glow Color - Set to pure black (#000000) to use the default color");
        }

        ImGui.SameLine(0.0f, innerSpacing);
        ImGui.TextUnformatted(label);

        if (changed)
        {
            colors = new ElezenStrings.Colour(ConvertBackColor(foregroundColor), ConvertBackColor(glowColor));
        }

        return changed;

        static Vector3 ConvertColor(uint color)
            => unchecked(new Vector3((byte)color / 255.0f, (byte)(color >> 8) / 255.0f, (byte)(color >> 16) / 255.0f));

        static uint ConvertBackColor(Vector3 color)
            => byte.CreateSaturating(color.X * 255.0f) | ((uint)byte.CreateSaturating(color.Y * 255.0f) << 8) | ((uint)byte.CreateSaturating(color.Z * 255.0f) << 16);
    }
}
