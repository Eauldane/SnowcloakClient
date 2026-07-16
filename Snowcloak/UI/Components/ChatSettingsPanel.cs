using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.Chat;

namespace Snowcloak.UI.Components;

public sealed class ChatSettingsPanel
{
    private readonly ChatClientService _chatService;
    private readonly SnowcloakConfigService _configService;

    public ChatSettingsPanel(ChatClientService chatService, SnowcloakConfigService configService)
    {
        _chatService = chatService;
        _configService = configService;
    }

    public void Draw()
    {
        var config = _configService.Current;
        var enabled = config.ChatEnabled;
        if (ImGui.Checkbox("Enable Snowcloak chat", ref enabled))
        {
            _configService.Update(value => value.ChatEnabled = enabled);
        }

        using (ImRaii.Disabled(!enabled))
        {
            var gameLog = config.ChatShowInGameLog;
            if (ImGui.Checkbox("Show messages in the in-game chat log", ref gameLog))
            {
                _configService.Update(value => value.ChatShowInGameLog = gameLog);
            }

            var dtr = config.ChatEnableDtrEntry;
            if (ImGui.Checkbox("Show unread count in the DTR bar", ref dtr))
            {
                _configService.Update(value => value.ChatEnableDtrEntry = dtr);
            }

            var sounds = config.ChatSoundsEnabled;
            if (ImGui.Checkbox("Play chat sounds", ref sounds))
            {
                _configService.Update(value => value.ChatSoundsEnabled = sounds);
            }

            ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
            if (ImGui.BeginCombo("Default chat sound", config.DefaultChatSound.ToString()))
            {
                foreach (var option in Enum.GetValues<ChatSoundOption>())
                {
                    if (ImGui.Selectable(option.ToString(), option == config.DefaultChatSound))
                    {
                        _configService.Update(value => value.DefaultChatSound = option);
                    }
                }

                ImGui.EndCombo();
            }

            var directToasts = config.ChatToastDirectMessages;
            if (ImGui.Checkbox("Show a notification for direct messages", ref directToasts))
            {
                _configService.Update(value => value.ChatToastDirectMessages = directToasts);
            }

            var mentionToasts = config.ChatToastMentions;
            if (ImGui.Checkbox("Show a notification when mentioned", ref mentionToasts))
            {
                _configService.Update(value => value.ChatToastMentions = mentionToasts);
            }

            var autoMute = config.AutoMuteNewSyncshellChats;
            if (ImGui.Checkbox("Auto-mute newly joined syncshell chats", ref autoMute))
            {
                _configService.Update(value => value.AutoMuteNewSyncshellChats = autoMute);
            }

            var vanity = config.ApplyVanityColoursToGameChat;
            if (ImGui.Checkbox("Apply vanity colours to the in-game chat log", ref vanity))
            {
                _configService.Update(value => value.ApplyVanityColoursToGameChat = vanity);
            }

            ImGuiHelpers.ScaledDummy(8f);
            ImGui.TextUnformatted("Conversation notifications");
            ImGui.Separator();
            foreach (var conversation in _chatService.Store.Snapshot.Conversations)
            {
                using var id = ImRaii.PushId(conversation.Key.ToString());
                var muted = conversation.Muted;
                if (ImGui.Checkbox("Mute " + conversation.Title, ref muted))
                {
                    _chatService.SetMuted(conversation.Key, muted);
                }

                ImGui.SameLine();
                var sound = _chatService.GetSound(conversation.Key);
                ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo("##sound", sound.ToString()))
                {
                    foreach (var option in Enum.GetValues<ChatSoundOption>())
                    {
                        if (ImGui.Selectable(option.ToString(), option == sound))
                        {
                            _chatService.SetSound(conversation.Key, option);
                        }
                    }

                    ImGui.EndCombo();
                }
            }
        }
    }
}
