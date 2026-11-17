using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Configuration;
using System.Numerics;

namespace Snowcloak.UI.Components;

internal static class SyncshellFirstUseWarning
{
    public static bool Draw(SnowcloakConfigService configService)
    {
        if (configService.Current.SyncshellWarningAcknowledged)
            return true;

        const string title = "Before using syncshells###SyncshellFirstUseWarning";
        if (!ImGui.IsPopupOpen(title))
            ImGui.OpenPopup(title);

        var maxSize = ImGui.GetMainViewport().WorkSize - new Vector2(32f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextWindowSize(Vector2.Min(new Vector2(600, 490) * ImGuiHelpers.GlobalScale, maxSize), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(Vector2.Min(new Vector2(320, 240) * ImGuiHelpers.GlobalScale, maxSize), maxSize);
        if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
            return false;

        using (var child = ImRaii.Child("warning-content", new Vector2(-1, -ImGui.GetFrameHeightWithSpacing()), false))
        {
            if (child)
            {
                ElezenImgui.WrappedText("Please be advised: Joining syncshells means that you'll be paired with potentially hundreds of users at a time.");
                ImGui.Spacing();
                ElezenImgui.WrappedText("As well as reducing performance in crowded areas, this means anyone in the same syncshells as you will be able to see your modded appearance, and you theirs, without directly pairing. This may expose you to things that aren't to your tastes.");
                ImGui.Spacing();
                ElezenImgui.WrappedText("It is also known that some unscrupulous users in the wider XIV modding community use plugins that can copy modded appearances in their entirety, and allow them to \"steal\" your appearance. There is no reliable way for syncs to detect users using these plugins - by joining a large syncshell, please be aware that you may run into users doing this.");
                ImGui.Spacing();
                ElezenImgui.WrappedText("Please bear these points in mind when using syncshells.");
                ImGui.Spacing();
                ElezenImgui.WrappedText("This warning will only display once.");
            }
        }

        if (ImGui.Button("I've read this and understand", new Vector2(-1, 0)))
        {
            configService.Update(config => config.SyncshellWarningAcknowledged = true);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
        return false;
    }
}
