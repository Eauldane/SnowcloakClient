using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace Snowcloak.UI;

public static class SnowcloakUi
{
    public static readonly ImGuiWindowFlags PopupWindowFlags = ImGuiWindowFlags.NoResize |
                                                               ImGuiWindowFlags.NoScrollbar |
                                                               ImGuiWindowFlags.NoScrollWithMouse;

    public static Vector4 AccentColor { get; set; } = ImGuiColors.DalamudYellow;

    public static Vector4 UploadColor((long Transferred, long Total) data) => data.Transferred == 0
        ? ImGuiColors.DalamudGrey
        : data.Transferred == data.Total
            ? ImGuiColors.ParsedGreen
            : ImGuiColors.DalamudYellow;

    internal static void DistanceSeparator()
    {
        ImGuiHelpers.ScaledDummy(5);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(5);
    }
}
