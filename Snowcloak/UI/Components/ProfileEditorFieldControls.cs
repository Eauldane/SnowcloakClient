using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Snowcloak.Core.Profiles;
using System.Numerics;

namespace Snowcloak.UI.Components;

public static class ProfileEditorFieldControls
{
    public static void DrawShortInput(string label, string value, Action<string> setValue, Action markDirty)
    {
        DrawFieldLabel(label);
        ImGui.SetNextItemWidth(-1f);
        var editedValue = value;
        if (ImGui.InputText($"##{label}", ref editedValue, ProfileEditSession.MaxShortTextLength))
        {
            setValue(editedValue);
            markDirty();
        }
    }

    public static void DrawMultiline(string label, string value, int maxLength, float height, Action<string> setValue, Action markDirty)
    {
        DrawFieldLabel(label);
        var editedValue = value;
        if (ImGui.InputTextMultiline($"##{label}", ref editedValue, maxLength, new Vector2(ImGui.GetContentRegionAvail().X, height)))
        {
            setValue(editedValue);
            markDirty();
        }
    }

    public static void DrawFieldLabel(string label, string? helper = null)
    {
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        if (!string.IsNullOrWhiteSpace(helper))
        {
            ImGui.TextWrapped(helper);
        }
    }
}
