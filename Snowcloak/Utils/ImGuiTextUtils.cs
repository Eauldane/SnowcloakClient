using Dalamud.Bindings.ImGui;

namespace Snowcloak.Utils;

public static class ImGuiTextUtils
{
    private const string Ellipsis = "...";
    
    public static string TruncateToWidth(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f)
        {
            return text;
        }

        if (ImGui.CalcTextSize(text).X <= maxWidth)
        {
            return text;
        }

        var ellipsisWidth = ImGui.CalcTextSize(Ellipsis).X;
        if (ellipsisWidth > maxWidth)
        {
            return Ellipsis;
        }

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidateWidth = ImGui.CalcTextSize(text[..mid]).X + ellipsisWidth;
            if (candidateWidth <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low <= 0 ? Ellipsis : text[..low] + Ellipsis;
    }
}
