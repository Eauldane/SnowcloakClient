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

    private readonly Lock _speedDialLock = new();

    public override string FileName => "chat-preferences.json";

    public void EnsureDefaults(IEnumerable<(ConversationKey Key, string Title)> conversations, bool autoMuteNewSyncshellChats)
    {
        var missing = conversations.Where(item => !Current.Conversations.TryGetValue(item.Key.ToString(), out var prefs)
            || !prefs.Muted.HasValue).ToArray();
        if (missing.Length == 0) return;
        Update(config =>
        {
            foreach (var item in missing)
            {
                var prefs = GetOrCreate(config, item.Key);
                prefs.Muted ??= item.Key.Kind == ConversationKind.Syncshell
                    && (RegionalSyncshellRegex().IsMatch(item.Title) || autoMuteNewSyncshellChats);
            }
        });
    }

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

    public ChatSoundOption? GetSoundOverride(ConversationKey key)
        => Current.Conversations.TryGetValue(key.ToString(), out var preferences) ? preferences.Sound : null;

    public void SetSound(ConversationKey key, ChatSoundOption? sound)
    {
        Update(config => GetOrCreate(config, key).Sound = sound);
    }

    public int GetSpeedDial(string server, string uid, ConversationKey key)
    {
        lock (_speedDialLock)
        {
            return Current.SpeedDialsByIdentity.TryGetValue(SpeedDialScope(server, uid), out var assignments)
                ? assignments.FirstOrDefault(item => string.Equals(item.Value, key.ToString(), StringComparison.Ordinal)).Key : 0;
        }
    }

    public ConversationKey? ResolveSpeedDial(string server, string uid, int number)
    {
        lock (_speedDialLock)
        {
            return Current.SpeedDialsByIdentity.TryGetValue(SpeedDialScope(server, uid), out var assignments)
                ? ChatSpeedDialPolicy.Resolve(assignments, number) : null;
        }
    }

    public bool TryAssignSpeedDial(string server, string uid, int number, ConversationKey key)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(uid)) return false;
        lock (_speedDialLock)
        {
            var scope = SpeedDialScope(server, uid);
            var assignments = Current.SpeedDialsByIdentity.TryGetValue(scope, out var current)
                ? new Dictionary<int, string>(current) : new Dictionary<int, string>();
            if (!ChatSpeedDialPolicy.TryAssign(assignments, number, key)) return false;
            Update(config => config.SpeedDialsByIdentity[scope] = assignments);
            return true;
        }
    }

    public void ClearSpeedDial(string server, string uid, int number)
    {
        lock (_speedDialLock)
        {
            var scope = SpeedDialScope(server, uid);
            if (!Current.SpeedDialsByIdentity.TryGetValue(scope, out var current) || !current.ContainsKey(number)) return;
            var assignments = new Dictionary<int, string>(current);
            assignments.Remove(number);
            Update(config => config.SpeedDialsByIdentity[scope] = assignments);
        }
    }

    private static string SpeedDialScope(string server, string uid)
        => System.Text.Json.JsonSerializer.Serialize(new[] { server, uid });

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
