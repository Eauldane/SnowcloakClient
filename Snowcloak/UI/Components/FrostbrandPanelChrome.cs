using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ElezenTools.UI;

namespace Snowcloak.UI.Components;

internal static class FrostbrandPanelChrome
{
    public static void DrawSectionTitle(FontAwesomeIcon icon, string title) => ModernSection.Header(icon, title);

    public static void DrawSoftSeparator() => ModernSection.SoftSeparator();

    public static List<string> WrapText(string text, float maxWidth)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && ImGui.CalcTextSize(candidate).X > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0)
            lines.Add(current);
        return lines;
    }
}
