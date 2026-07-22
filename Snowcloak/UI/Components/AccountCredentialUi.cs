using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using ElezenTools.UI;
using System.Numerics;

namespace Snowcloak.UI.Components;

public static class AccountCredentialUi
{
    private static readonly Vector4 AccentColor = new(0.34f, 0.68f, 0.92f, 1f);

    public static void DrawHeader(string title, string description)
    {
        ImGui.TextColored(AccentColor, title);
        ImGui.Separator();
        ImGui.TextWrapped(description);
        ImGuiHelpers.ScaledDummy(new Vector2(0, 4));
    }

    public static void DrawTextInput(string id, string label, string hint, ref string value, int maxLength,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint($"##{id}", hint, ref value, maxLength, flags);
    }

    public static void DrawPasswordInput(string id, string label, string hint, ref string value, int maxLength,
        bool showPassword)
    {
        DrawTextInput(id, label, hint, ref value, maxLength,
            showPassword ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password);
    }

    public static void DrawPasswordVisibilityToggle(string id, ref bool showPassword)
    {
        ImGui.Checkbox($"Show password##{id}", ref showPassword);
    }

    public static bool DrawPrimaryButton(string id, string label)
    {
        ImGuiHelpers.ScaledDummy(new Vector2(0, 3));
        return ImGui.Button($"{label}##{id}", new Vector2(ImGui.GetContentRegionAvail().X, 0));
    }

    public static void DrawRequirements(bool includePassword)
    {
        var text = includePassword
            ? "Username: 3-64 characters with no spaces. Password: at least 8 characters."
            : "Username: 3-64 characters with no spaces.";
        ElezenImgui.DrawHelpText(text);
    }
}
