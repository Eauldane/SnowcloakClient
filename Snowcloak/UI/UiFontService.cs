using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin;
using ElezenTools.UI;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class UiFontService : IDisposable
{
    public UiFontService(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);

        UidFont = pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk => tk.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkMedium, new()
            {
                SizePx = 35,
                GlyphRanges = [0x20, 0x7E, 0]
            }));
        });
        GameFont = pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new(GameFontFamilyAndSize.Axis12));
        IconFont = pluginInterface.UiBuilder.IconFontFixedWidthHandle;
    }

    public IFontHandle GameFont { get; }
    public IFontHandle IconFont { get; }
    public IFontHandle UidFont { get; }

    public void BigText(string text, Vector4? colour = null)
    {
        ElezenImgui.FontText(text, UidFont, colour);
    }

    public void Dispose()
    {
        UidFont.Dispose();
        GameFont.Dispose();
    }
}
