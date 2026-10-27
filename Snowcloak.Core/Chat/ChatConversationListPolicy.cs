namespace Snowcloak.Core.Chat;

public static class ChatConversationListPolicy
{
    public static bool HasMessages(ConversationSnapshot conversation)
        => conversation.Entries.Any(entry => entry.Kind == ChatEntryKind.Message);

    public static bool IsVisible(ConversationSnapshot conversation, ConversationKey? active)
        => conversation.Key.Kind != ConversationKind.Direct || HasMessages(conversation)
            || !string.IsNullOrWhiteSpace(conversation.Draft) || conversation.Key == active;

    public static IReadOnlyList<ConversationSnapshot> Order(IEnumerable<ConversationSnapshot> conversations, ConversationKey? active)
        => conversations.Where(conversation => IsVisible(conversation, active))
            .OrderBy(conversation => conversation.Key.Kind switch
            {
                ConversationKind.Room => 0,
                ConversationKind.Direct => 1,
                _ => 2,
            })
            .ThenByDescending(conversation => conversation.Key.Kind != ConversationKind.Direct ? DateTimeOffset.MinValue
                : conversation.Entries.Where(entry => entry.Kind == ChatEntryKind.Message)
                    .Select(entry => entry.Timestamp).DefaultIfEmpty(DateTimeOffset.MaxValue).Max())
            .ThenBy(conversation => conversation.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(conversation => conversation.Key.Id, StringComparer.Ordinal).ToArray();

    public static bool NeedsHistory(ConversationSnapshot conversation, string digestMessageId, string? knownMessageId)
        => (conversation.Key.Kind == ConversationKind.Direct && !conversation.HistoryLoaded
                && !conversation.Entries.Any(entry => entry.Kind == ChatEntryKind.Message && entry.State == DeliveryState.Sent))
            || !string.Equals(digestMessageId, knownMessageId, StringComparison.Ordinal);

    public static bool ShouldNotifyReplay(string? previousMessageId, string digestMessageId)
        => !string.IsNullOrEmpty(previousMessageId) && !string.Equals(previousMessageId, digestMessageId, StringComparison.Ordinal);
}
