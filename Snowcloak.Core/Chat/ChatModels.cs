namespace Snowcloak.Core.Chat;

using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Roleplay;
using System.Numerics;

public enum ConversationKind
{
    Direct,
    Syncshell,
    Room,
}

public readonly record struct ConversationKey(ConversationKind Kind, string Id)
{
    public override string ToString() => $"{(int)Kind}:{Id}";

    public static bool TryParse(string? value, out ConversationKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1
            || !int.TryParse(value.AsSpan(0, separator), out var kind)
            || !Enum.IsDefined((ConversationKind)kind))
        {
            return false;
        }

        key = new ConversationKey((ConversationKind)kind, value[(separator + 1)..]);
        return true;
    }
}

public enum DeliveryState
{
    Pending,
    Sent,
    Failed,
}

public enum ChatEntryKind
{
    Message,
    MemberJoined,
    MemberLeft,
    TurnChanged,
}

public abstract record ChatSegment(string Value);

public sealed record TextSegment(string Text) : ChatSegment(Text);

public sealed record LinkSegment(string Address, string Text) : ChatSegment(Text);

public sealed record MentionSegment(string Uid) : ChatSegment(Uid);

public sealed record EmoteSegment(string Name) : ChatSegment(Name);

public sealed record BbSegment(string Markup) : ChatSegment(Markup);

public sealed record SenderDisplay(
    string Name,
    Vector4? Colour = null,
    Vector4? Glow = null,
    string ForegroundHex = "",
    string GlowHex = "");

public sealed record ChatEntry(
    string LocalId,
    string MessageId,
    string SenderUid,
    DateTimeOffset Timestamp,
    IReadOnlyList<ChatSegment> Segments,
    string RawText,
    DeliveryState State,
    SenderDisplay Display,
    bool IsEmote = false,
    ChatEntryKind Kind = ChatEntryKind.Message,
    RpChatMode RpMode = RpChatMode.Standard,
    RoomDiceRollDto? DiceRoll = null,
    RoomTurnStateDto? TurnState = null);

public sealed class ChatMessageSentEventArgs(ConversationKey key, ChatEntry entry) : EventArgs
{
    public ConversationKey Key { get; } = key;
    public ChatEntry Entry { get; } = entry;
}

public sealed record ConversationSnapshot(
    ConversationKey Key,
    string Title,
    IReadOnlyList<ChatEntry> Entries,
    IReadOnlyDictionary<string, RoomRole> Members,
    IReadOnlyDictionary<string, IReadOnlyList<string>> MemberLabels,
    int Unread,
    string Draft,
    bool Muted,
    bool HistoryLoaded);

public sealed record ChatStoreSnapshot(
    IReadOnlyList<ConversationSnapshot> Conversations,
    ConversationKey? ActiveConversation)
{
    public int TotalUnread => Conversations.Sum(conversation => conversation.Muted ? 0 : conversation.Unread);
}
