using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;

namespace Snowcloak.Game.Nameplates;

public static class NameplateDecorationWriter
{
    public static void Apply(INamePlateUpdateHandler handler, SeString start, SeString end, string? suffix = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (string.IsNullOrEmpty(suffix))
        {
            handler.NameParts.TextWrap = (start, end);
            return;
        }

        var builder = new SeStringBuilder();
        builder.Append(end);
        builder.AddText(suffix);
        handler.NameParts.TextWrap = (start, builder.Build());
    }
}
