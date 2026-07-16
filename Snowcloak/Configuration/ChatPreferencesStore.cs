using Snowcloak.Configuration.Configurations;
using Snowcloak.Configuration.Models;
using Snowcloak.Core.Chat;
using System.Text.RegularExpressions;

namespace Snowcloak.Configuration;

public sealed partial class ChatPreferencesStore : StateDocument<ChatPreferencesConfig>
{
    public ChatPreferencesStore(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => "chat-preferences.json";

    public bool ResolveMuted(ConversationKey key, string title, bool autoMuteNewSyncshellChats)
    {
        if (Current.Conversations.TryGetValue(key.ToString(), out var preferences) && preferences.Muted.HasValue)
        {
            return preferences.Muted.Value;
        }

        var muted = key.Kind == ConversationKind.Syncshell
                    && (RegionalSyncshellRegex().IsMatch(title) || autoMuteNewSyncshellChats);
        Update(config => GetOrCreate(config, key).Muted = muted);
        return muted;
    }

    public ChatSoundOption ResolveSound(ConversationKey key, ChatSoundOption defaultSound)
        => Current.Conversations.TryGetValue(key.ToString(), out var preferences) && preferences.Sound.HasValue
            ? preferences.Sound.Value
            : defaultSound;

    public void SetMuted(ConversationKey key, bool muted)
    {
        Update(config => GetOrCreate(config, key).Muted = muted);
    }

    public void SetSound(ConversationKey key, ChatSoundOption sound)
    {
        Update(config => GetOrCreate(config, key).Sound = sound);
    }

    private static ConversationPrefs GetOrCreate(ChatPreferencesConfig config, ConversationKey key)
    {
        var serialised = key.ToString();
        if (!config.Conversations.TryGetValue(serialised, out var preferences))
        {
            preferences = new ConversationPrefs();
            config.Conversations[serialised] = preferences;
        }

        return preferences;
    }

    [GeneratedRegex("^Snowcloak - .* Public Syncshell$", RegexOptions.Compiled | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex RegionalSyncshellRegex();
}
