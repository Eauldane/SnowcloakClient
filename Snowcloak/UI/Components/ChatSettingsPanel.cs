using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using ElezenTools.UI;
using Snowcloak.Configuration;

namespace Snowcloak.UI.Components;

public sealed class ChatSettingsPanel
{
    private readonly SnowcloakConfigService _configService;
    private readonly UiFontService _fontService;
    private readonly Dictionary<string, object> _selectedComboItems = new(StringComparer.Ordinal);

    public ChatSettingsPanel(SnowcloakConfigService configService, UiFontService fontService)
    {
        _configService = configService;
        _fontService = fontService;
    }

    public void Draw()
    {
        _fontService.BigText("Chat Settings");

        var disableChat = _configService.Current.DisableChat;
        if (ImGui.Checkbox("Disable chat globally", ref disableChat))
        {
            _configService.Update(c => c.DisableChat = disableChat);
        }
        ElezenImgui.DrawHelpText("Global setting to disable chat.");

        var applyVanityColoursToGameChat = _configService.Current.ApplyVanityColoursToGameChat;
        if (ImGui.Checkbox("Apply vanity colours to names in chat", ref applyVanityColoursToGameChat))
        {
            _configService.Update(c => c.ApplyVanityColoursToGameChat = applyVanityColoursToGameChat);
        }
        ElezenImgui.DrawHelpText("Colours player names in normal game chat when they match paired users.");

        ImGui.Separator();
        _fontService.BigText("Message Sounds");
        DrawChatSoundSetting(
            "Direct message sound",
            "DirectChatSound",
            _configService.Current.SnowChatDirectSound,
            option => _configService.Update(c => c.SnowChatDirectSound = option),
            "Choose which in-game sound effect or Snowcloak extra plays when you receive a direct SnowChat message.");
        DrawChatSoundSetting(
            "Group chat sound",
            "GroupChatSound",
            _configService.Current.SnowChatGroupSound,
            option => _configService.Update(c => c.SnowChatGroupSound = option),
            "Choose which in-game sound effect or Snowcloak extra plays when you receive a syncshell or standard channel SnowChat message.");

        ImGui.TextWrapped("The chat system is currently under active development. If you use it, you're encouraged to check back here often"
                          + "to see if there's any new settings to play with!");
    }

    private void DrawChatSoundSetting(string label, string id, ChatWindow.ChatSoundOption currentOption, Action<ChatWindow.ChatSoundOption> updateOption, string helpText)
    {
        ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
        SettingsUiControls.DrawCombo(
            $"{label}##{id}",
            ChatWindow.GetAvailableChatSoundOptions(currentOption),
            ChatWindow.GetChatSoundOptionLabel,
            _selectedComboItems,
            option => updateOption(option),
            currentOption);
        ElezenImgui.DrawHelpText(helpText);
    }
}
