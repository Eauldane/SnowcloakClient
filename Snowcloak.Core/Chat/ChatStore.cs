namespace Snowcloak.Core.Chat;

using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Chat;

public interface IChatTransport
{
    Task<ChatMessageDto> SendAsync(ConversationKey key, string text, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatMessageDto>> GetHistoryAsync(ConversationKey key, CancellationToken cancellationToken);
}

public interface IChatIdentityResolver
{
    string SelfUid { get; }
    SenderDisplay Resolve(UserData user);
}

public sealed class ChatStore
{
    private const int Capacity = 500;
    private readonly IChatIdentityResolver _identityResolver;
    private readonly IChatTransport _transport;
    private readonly Lock _lock = new();
    private readonly Dictionary<ConversationKey, ConversationState> _conversations = [];
    private readonly Dictionary<ConversationKey, Task> _historyLoads = [];
    private ConversationKey? _activeConversation;

    public ChatStore(IChatTransport transport, IChatIdentityResolver identityResolver)
    {
        _transport = transport;
        _identityResolver = identityResolver;
    }

    public event EventHandler? Changed;
    public event EventHandler<ChatMessageSentEventArgs>? MessageSent;

    public ChatStoreSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return new ChatStoreSnapshot(
                    _conversations.Values
                        .Select(static conversation => conversation.ToSnapshot())
                        .OrderBy(static conversation => conversation.Key.Kind)
                        .ThenBy(static conversation => conversation.Title, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    _activeConversation);
            }
        }
    }

    public ConversationSnapshot GetOrCreate(ConversationKey key, string? title = null, bool muted = false)
    {
        ConversationSnapshot snapshot;
        var changed = false;
        lock (_lock)
        {
            var conversation = GetOrCreateState(key, title, muted, out changed);
            snapshot = conversation.ToSnapshot();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return snapshot;
    }

    public void SetConversationMetadata(ConversationKey key, string title, bool muted)
    {
        var changed = false;
        lock (_lock)
        {
            var conversation = GetOrCreateState(key, title, muted, out changed);
            if (!string.Equals(conversation.Title, title, StringComparison.Ordinal) || conversation.Muted != muted)
            {
                conversation.Title = title;
                conversation.Muted = muted;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemoveConversation(ConversationKey key)
    {
        var changed = false;
        lock (_lock)
        {
            changed = _conversations.Remove(key);
            _historyLoads.Remove(key);
            if (_activeConversation == key)
            {
                _activeConversation = null;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public ChatEntry? AppendIncoming(ConversationKey key, ChatMessageDto message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ChatEntry? entry = null;
        lock (_lock)
        {
            var conversation = GetOrCreateState(key, null, false, out _);
            if (AppendStamped(conversation, message, countUnread: _activeConversation != key))
            {
                entry = conversation.Entries.First(candidate => string.Equals(candidate.MessageId, message.MessageId, StringComparison.Ordinal));
            }
        }

        if (entry != null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return entry;
    }

    public async Task SendAsync(ConversationKey key, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var localId = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            var conversation = GetOrCreateState(key, null, false, out _);
            conversation.Entries.Add(new ChatEntry(
                localId,
                string.Empty,
                _identityResolver.SelfUid,
                DateTimeOffset.UtcNow,
                ChatMessageCodec.Decode(text),
                text,
                DeliveryState.Pending,
                new SenderDisplay("You"),
                IsEmote(text)));
            Trim(conversation.Entries);
            conversation.Draft = string.Empty;
        }

        Changed?.Invoke(this, EventArgs.Empty);

        ChatEntry? sent = null;
        try
        {
            var stamped = await _transport.SendAsync(key, text, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (!_conversations.TryGetValue(key, out var conversation))
                {
                    return;
                }

                var pendingIndex = conversation.Entries.FindIndex(entry => string.Equals(entry.LocalId, localId, StringComparison.Ordinal));
                if (pendingIndex < 0)
                {
                    return;
                }

                var duplicateIndex = conversation.Entries.FindIndex(entry => !string.IsNullOrEmpty(stamped.MessageId)
                    && string.Equals(entry.MessageId, stamped.MessageId, StringComparison.Ordinal));
                if (duplicateIndex >= 0 && duplicateIndex != pendingIndex)
                {
                    conversation.Entries.RemoveAt(pendingIndex);
                }
                else
                {
                    conversation.Entries[pendingIndex] = CreateEntry(stamped, localId);
                }

                sent = conversation.Entries.FirstOrDefault(entry => string.Equals(entry.MessageId, stamped.MessageId, StringComparison.Ordinal));
            }
        }
        catch
        {
            lock (_lock)
            {
                if (_conversations.TryGetValue(key, out var conversation))
                {
                    var index = conversation.Entries.FindIndex(entry => string.Equals(entry.LocalId, localId, StringComparison.Ordinal));
                    if (index >= 0)
                    {
                        conversation.Entries[index] = conversation.Entries[index] with { State = DeliveryState.Failed };
                    }
                }
            }

            throw;
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (sent != null)
        {
            MessageSent?.Invoke(this, new ChatMessageSentEventArgs(key, sent));
        }
    }

    public Task RetryAsync(ConversationKey key, string localId, CancellationToken cancellationToken = default)
    {
        string? text = null;
        lock (_lock)
        {
            if (_conversations.TryGetValue(key, out var conversation))
            {
                var entry = conversation.Entries.FirstOrDefault(candidate => string.Equals(candidate.LocalId, localId, StringComparison.Ordinal));
                if (entry?.State == DeliveryState.Failed)
                {
                    text = entry.RawText;
                    conversation.Entries.Remove(entry);
                }
            }
        }

        return text == null ? Task.CompletedTask : SendAsync(key, text, cancellationToken);
    }

    public Task EnsureHistory(ConversationKey key, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var conversation = GetOrCreateState(key, null, false, out _);
            if (conversation.HistoryLoaded)
            {
                return Task.CompletedTask;
            }

            if (_historyLoads.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var load = LoadHistoryAsync(key, cancellationToken);
            _historyLoads[key] = load;
            return load;
        }
    }

    public void InvalidateHistory()
    {
        var changed = false;
        lock (_lock)
        {
            foreach (var conversation in _conversations.Values)
            {
                changed |= conversation.HistoryLoaded;
                conversation.HistoryLoaded = false;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetActive(ConversationKey? key)
    {
        lock (_lock)
        {
            _activeConversation = key;
            if (key.HasValue && _conversations.TryGetValue(key.Value, out var conversation))
            {
                conversation.Unread = 0;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkRead(ConversationKey key)
    {
        lock (_lock)
        {
            if (_conversations.TryGetValue(key, out var conversation))
            {
                conversation.Unread = 0;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetMuted(ConversationKey key, bool muted)
    {
        lock (_lock)
        {
            var conversation = GetOrCreateState(key, null, muted, out _);
            conversation.Muted = muted;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetDraft(ConversationKey key, string draft)
    {
        lock (_lock)
        {
            var conversation = GetOrCreateState(key, null, false, out _);
            conversation.Draft = draft;
        }
    }

    public void ReplaceMembers(ConversationKey key, IEnumerable<KeyValuePair<string, RoomRole>> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        lock (_lock)
        {
            var conversation = GetOrCreateState(key, null, false, out _);
            conversation.Members.Clear();
            foreach (var member in members)
            {
                conversation.Members[member.Key] = member.Value;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetMember(ConversationKey key, string uid, RoomRole role)
    {
        lock (_lock)
        {
            GetOrCreateState(key, null, false, out _).Members[uid] = role;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ReplaceMemberLabels(ConversationKey key, IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        lock (_lock)
        {
            var conversation = GetOrCreateState(key, null, false, out _);
            conversation.MemberLabels.Clear();
            foreach (var member in members)
            {
                conversation.MemberLabels[member.Key] = member.Value.ToArray();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveMember(ConversationKey key, string uid)
    {
        lock (_lock)
        {
            if (_conversations.TryGetValue(key, out var conversation))
            {
                conversation.Members.Remove(uid);
                conversation.MemberLabels.Remove(uid);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshDisplays(Func<string, SenderDisplay?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_lock)
        {
            foreach (var conversation in _conversations.Values)
            {
                for (var index = 0; index < conversation.Entries.Count; index++)
                {
                    var entry = conversation.Entries[index];
                    var display = resolver(entry.SenderUid);
                    if (display != null)
                    {
                        conversation.Entries[index] = entry with { Display = display };
                    }
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadHistoryAsync(ConversationKey key, CancellationToken cancellationToken)
    {
        try
        {
            var history = await _transport.GetHistoryAsync(key, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                var conversation = GetOrCreateState(key, null, false, out _);
                foreach (var message in history.OrderBy(static message => message.Timestamp))
                {
                    AppendStamped(conversation, message, countUnread: false);
                }

                conversation.Entries.Sort(CompareEntries);
                Trim(conversation.Entries);
                conversation.HistoryLoaded = true;
            }
        }
        finally
        {
            lock (_lock)
            {
                _historyLoads.Remove(key);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool AppendStamped(ConversationState conversation, ChatMessageDto message, bool countUnread)
    {
        if (!string.IsNullOrEmpty(message.MessageId)
            && conversation.Entries.Any(entry => string.Equals(entry.MessageId, message.MessageId, StringComparison.Ordinal)))
        {
            return false;
        }

        conversation.Entries.Add(CreateEntry(message, Guid.NewGuid().ToString("N")));
        conversation.Entries.Sort(CompareEntries);
        Trim(conversation.Entries);
        if (countUnread && !string.Equals(message.Sender.UID, _identityResolver.SelfUid, StringComparison.Ordinal))
        {
            conversation.Unread++;
        }

        return true;
    }

    private ChatEntry CreateEntry(ChatMessageDto message, string localId)
    {
        var raw = ChatMessageCodec.DecodeText(message.Message.PayloadContent);
        return new ChatEntry(
            localId,
            message.MessageId,
            message.Sender.UID,
            DateTimeOffset.FromUnixTimeSeconds(message.Timestamp),
            ChatMessageCodec.Decode(raw),
            raw,
            DeliveryState.Sent,
            _identityResolver.Resolve(message.Sender),
            IsEmote(raw));
    }

    private ConversationState GetOrCreateState(ConversationKey key, string? title, bool muted, out bool created)
    {
        if (_conversations.TryGetValue(key, out var existing))
        {
            created = false;
            if (!string.IsNullOrWhiteSpace(title))
            {
                existing.Title = title;
            }

            return existing;
        }

        var conversation = new ConversationState(key, string.IsNullOrWhiteSpace(title) ? key.Id : title, muted);
        _conversations.Add(key, conversation);
        created = true;
        return conversation;
    }

    private static bool IsEmote(string text) => text.StartsWith("/me ", StringComparison.OrdinalIgnoreCase);

    private static int CompareEntries(ChatEntry left, ChatEntry right)
    {
        var timestamp = left.Timestamp.CompareTo(right.Timestamp);
        return timestamp != 0 ? timestamp : string.Compare(left.MessageId, right.MessageId, StringComparison.Ordinal);
    }

    private static void Trim(List<ChatEntry> entries)
    {
        if (entries.Count > Capacity)
        {
            entries.RemoveRange(0, entries.Count - Capacity);
        }
    }

    private sealed class ConversationState(ConversationKey key, string title, bool muted)
    {
        public ConversationKey Key { get; } = key;
        public string Title { get; set; } = title;
        public List<ChatEntry> Entries { get; } = [];
        public Dictionary<string, RoomRole> Members { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<string>> MemberLabels { get; } = new(StringComparer.Ordinal);
        public int Unread { get; set; }
        public string Draft { get; set; } = string.Empty;
        public bool Muted { get; set; } = muted;
        public bool HistoryLoaded { get; set; }

        public ConversationSnapshot ToSnapshot() => new(
            Key,
            Title,
            Entries.ToArray(),
            new Dictionary<string, RoomRole>(Members, StringComparer.Ordinal),
            MemberLabels.ToDictionary(member => member.Key, member => (IReadOnlyList<string>)member.Value.ToArray(), StringComparer.Ordinal),
            Unread,
            Draft,
            Muted,
            HistoryLoaded);
    }
}
