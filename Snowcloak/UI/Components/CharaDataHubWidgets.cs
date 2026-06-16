using Dalamud.Interface.Utility;
using ElezenTools.UI;
using Snowcloak.Configuration;

namespace Snowcloak.UI.Components;

internal static class CharaDataHubWidgets
{
    public static void DrawHelpFoldout(CharaDataConfigService configService, string text)
    {
        if (configService.Current.ShowHelpTexts)
        {
            ImGuiHelpers.ScaledDummy(5);
            ElezenImgui.DrawTree("What is this? (Explanation / Help)", () =>
            {
                ElezenImgui.WrappedText(text);
            });
        }
    }
}
