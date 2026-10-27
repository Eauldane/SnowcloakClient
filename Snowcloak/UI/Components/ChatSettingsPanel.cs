using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Configuration.Configurations;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Core.Chat;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Chat;
using System.Numerics;

namespace Snowcloak.UI.Components;

public sealed class ChatSettingsPanel(ChatClientService chatService, SnowcloakConfigService configService,
    ChatSoundPlayer soundPlayer, ChatIdentityResolver identityResolver, PairManager pairManager)
{
    private string _search = string.Empty;
    private int _kindFilter;
    private bool _mutedOnly;
    private string _activeTab = "In-game chat";

    public void Draw()
    {
        var config = configService.Current;
        var enabled = config.ChatEnabled;
        if (ImGui.Checkbox("Enable Snowcloak chat", ref enabled))
            configService.Update(value => value.ChatEnabled = enabled);
        using (ImRaii.Disabled(!enabled))
        {
            _activeTab = ModernTabBar.Draw("chatSettingsTabs",
                new[] { "In-game chat", "Notifications", "Conversation overrides" }, _activeTab);
            ImGuiHelpers.ScaledDummy(new Vector2(0, 5));
            if (_activeTab == "In-game chat") DrawGameChat();
            else if (_activeTab == "Notifications") DrawNotifications();
            else DrawOverrides();
        }
    }

    private void DrawGameChat()
    {
        var config = configService.Current;
        var gameLog = config.ChatShowInGameLog;
        if (ImGui.Checkbox("Show Snowcloak messages in the game chat log", ref gameLog))
            configService.Update(value => value.ChatShowInGameLog = gameLog);
        using (ImRaii.Disabled(!gameLog)) DrawColour();
    }

    private void DrawNotifications()
    {
        var config = configService.Current;
        var dtr = config.ChatEnableDtrEntry;
        if (ImGui.Checkbox("Show unread count in the DTR bar", ref dtr))
            configService.Update(value => value.ChatEnableDtrEntry = dtr);
        var sounds = config.ChatSoundsEnabled;
        if (ImGui.Checkbox("Play chat sounds", ref sounds))
            configService.Update(value => value.ChatSoundsEnabled = sounds);
        using (ImRaii.Disabled(!sounds))
        {
            ImGui.SetNextItemWidth(140f * ImGuiHelpers.GlobalScale);
            if (SoundCombo("Default sound", config.DefaultChatSound, false, out var sound))
                configService.Update(value => value.DefaultChatSound = sound!.Value);
            ImGui.SameLine();
            if (ImGui.SmallButton("Preview sound")) soundPlayer.Play(configService.Current.DefaultChatSound);
            var volume = Math.Clamp(config.ChatSoundVolume, 0, 100);
            ImGui.SetNextItemWidth(240f * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("Sound volume", ref volume, 0, 100, "%d%%"))
                configService.Update(value => value.ChatSoundVolume = volume);
            Tooltip("Percentage of game volume to play Snowcloak chat alerts");
        }
        var direct = config.ChatToastDirectMessages;
        if (ImGui.Checkbox("Show a popup for direct messages", ref direct))
            configService.Update(value => value.ChatToastDirectMessages = direct);
        var mentions = config.ChatToastMentions;
        if (ImGui.Checkbox("Show a popup when mentioned", ref mentions))
            configService.Update(value => value.ChatToastMentions = mentions);
    }

    private void DrawOverrides()
    {
        var config = configService.Current;
        var autoMute = config.AutoMuteNewSyncshellChats;
        if (ImGui.Checkbox("Mute alerts for newly joined syncshells", ref autoMute))
            configService.Update(value => value.AutoMuteNewSyncshellChats = autoMute);
        Tooltip("Applies to newly joined syncshells only.");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##conversation-search", "Search conversations...", ref _search, 256);
        ImGui.SetNextItemWidth(170f * ImGuiHelpers.GlobalScale);
        ImGui.Combo("##conversation-kind", ref _kindFilter, "All\0Direct messages\0Syncshells\0Rooms\0");
        ImGui.SameLine();
        ImGui.Checkbox("Muted only", ref _mutedOnly);
        DrawConversations(config.ChatSoundsEnabled);
    }

    private void DrawColour()
    {
        var packed = configService.Current.ChatReceivedMessageColour ?? SnowcloakConfig.DefaultChatReceivedMessageColour;
        var rgb = packed;
        var colour = ToVector(rgb);
        if (ImGui.ColorEdit3("Received message colour", ref colour, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.Uint8))
        {
            packed = (uint)byte.CreateSaturating(colour.X * 255f)
                | ((uint)byte.CreateSaturating(colour.Y * 255f) << 8)
                | ((uint)byte.CreateSaturating(colour.Z * 255f) << 16);
            configService.Update(value => value.ChatReceivedMessageColour = packed);
        }
        ImGui.TextUnformatted("Preview:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(ToVector(packed), 1f), "[SnowChat] Thancred: This is Thancred.");
    }

    private void DrawConversations(bool sounds)
    {
        var conversations = chatService.Store.Snapshot.Conversations
            .Where(c => (!_mutedOnly || c.Muted) && (_kindFilter == 0 || (int)c.Key.Kind == _kindFilter - 1))
            .Select(c => (Conversation: c, Title: ResolveTitle(c)))
            .Where(c => c.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || c.Conversation.Key.Id.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!ImGui.BeginTable("conversation-overrides", 3,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(0, 240f * ImGuiHelpers.GlobalScale))) return;
        ImGui.TableSetupColumn("Conversation", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mute alerts", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Sound", ImGuiTableColumnFlags.WidthFixed, 140f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var item in conversations)
        {
            var conversation = item.Conversation;
            using var id = ImRaii.PushId(conversation.Key.ToString());
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var icon = conversation.Key.Kind switch
            {
                ConversationKind.Direct => FontAwesomeIcon.User,
                ConversationKind.Syncshell => FontAwesomeIcon.PeopleGroup,
                _ => FontAwesomeIcon.Comments,
            };
            using (ImRaii.PushFont(UiBuilder.IconFont)) ImGui.TextUnformatted(icon.ToIconString());
            Tooltip(conversation.Key.Kind.ToString());
            ImGui.SameLine();
            ImGui.TextUnformatted(item.Title);
            Tooltip(item.Title);
            ImGui.TableNextColumn();
            var muted = conversation.Muted;
            if (ImGui.Checkbox("##mute", ref muted)) chatService.SetMuted(conversation.Key, muted);
            Tooltip("Suppress sounds and popups, and exclude this conversation from the DTR unread total. Messages still appear in the game chat.");
            ImGui.TableNextColumn();
            using (ImRaii.Disabled(muted || !sounds))
            {
                ImGui.SetNextItemWidth(-1);
                if (SoundCombo("##sound", chatService.GetSoundOverride(conversation.Key), true, out var sound))
                    chatService.SetSound(conversation.Key, sound);
            }
        }
        if (conversations.Length == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted("No matching conversations.");
        }
        ImGui.EndTable();
    }

    private string ResolveTitle(ConversationSnapshot conversation)
    {
        if (conversation.Key.Kind == ConversationKind.Direct)
            return identityResolver.Resolve(conversation.Key.Id)?.Name ?? conversation.Title;
        if (conversation.Key.Kind == ConversationKind.Syncshell)
            return pairManager.Groups.Values.FirstOrDefault(group =>
                string.Equals(group.GID, conversation.Key.Id, StringComparison.Ordinal))?.GroupAliasOrGID ?? conversation.Title;
        var room = chatService.ListRooms().FirstOrDefault(room => string.Equals(room.RoomId, conversation.Key.Id, StringComparison.Ordinal));
        return room?.Scene?.IsScene == true && !string.IsNullOrWhiteSpace(room.Scene.Title)
            ? room.Name + " - " + room.Scene.Title : conversation.Title;
    }

    private static bool SoundCombo(string label, ChatSoundOption? current, bool allowDefault, out ChatSoundOption? selected)
    {
        selected = current;
        var changed = false;
        if (ImGui.BeginCombo(label, current?.ToString() ?? "Default"))
        {
            if (allowDefault && ImGui.Selectable("Default", !current.HasValue))
            {
                selected = null;
                changed = true;
            }
            foreach (var option in Enum.GetValues<ChatSoundOption>())
            {
                if (ImGui.Selectable(option.ToString(), option == current))
                {
                    selected = option;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    private static Vector3 ToVector(uint rgb) => new((rgb & 255) / 255f, ((rgb >> 8) & 255) / 255f, ((rgb >> 16) & 255) / 255f);

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }
}
