using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;

namespace Snowcloak.Game.Nameplates;

public static class NameplateDecorationWriter
{
    public static void Apply(INamePlateUpdateHandler handler, SeString start, SeString end)
    {
        ArgumentNullException.ThrowIfNull(handler);

        handler.NameParts.TextWrap = (start, end);
    }
}
