using Snowcloak.Configuration.Configurations;
using Snowcloak.Core.Chat;

namespace Snowcloak.Configuration;

public sealed class ChatSyncStateStore : StateDocument<ChatSyncStateConfig>
{
    public ChatSyncStateStore(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => "chat-sync-state.json";

    public IReadOnlyDictionary<ConversationKey, string> GetHistoryHeads(string server)
    {
        if (!Current.HistoryHeadsByServer.TryGetValue(server, out var values))
        {
            return new Dictionary<ConversationKey, string>();
        }

        return values
            .Select(entry => new { Parsed = ConversationKey.TryParse(entry.Key, out var key), Key = key, entry.Value })
            .Where(entry => entry.Parsed)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    public void ReplaceHistoryHeads(string server, IReadOnlyDictionary<ConversationKey, string> heads)
    {
        Update(config => config.HistoryHeadsByServer[server] = heads.ToDictionary(
            entry => entry.Key.ToString(),
            entry => entry.Value,
            StringComparer.Ordinal));
    }

    public void SetHistoryHead(string server, ConversationKey key, string messageId)
    {
        Update(config =>
        {
            if (!config.HistoryHeadsByServer.TryGetValue(server, out var heads))
            {
                heads = new Dictionary<string, string>(StringComparer.Ordinal);
                config.HistoryHeadsByServer[server] = heads;
            }

            heads[key.ToString()] = messageId;
        });
    }
}
