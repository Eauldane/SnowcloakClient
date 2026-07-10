using Snowcloak.API.Dto.Session;

namespace Snowcloak.WebAPI.SignalR;

internal sealed class SessionResumeState
{
    private const int RecentCapacity = 256;
    private readonly SortedDictionary<long, Func<Task>> _buffer = [];
    private readonly Queue<long> _recent = [];
    private readonly HashSet<long> _seen = [];
    private readonly Lock _lock = new();
    private bool _buffering;

    public string SessionId { get; private set; } = string.Empty;
    public long LastSequence { get; private set; }

    public void BeginBuffering()
    {
        lock (_lock)
        {
            _buffering = true;
        }
    }

    public void Establish(string sessionId)
    {
        lock (_lock)
        {
            if (string.Equals(SessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            SessionId = sessionId;
            LastSequence = 0;
            _buffer.Clear();
            _recent.Clear();
            _seen.Clear();
            _buffering = false;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            SessionId = string.Empty;
            LastSequence = 0;
            _buffer.Clear();
            _recent.Clear();
            _seen.Clear();
            _buffering = false;
        }
    }

    public SessionResumeRequestDto CreateRequest()
    {
        lock (_lock)
        {
            return new SessionResumeRequestDto
            {
                SessionId = SessionId,
                LastSequence = LastSequence,
                RecentSequences = _recent.ToList(),
            };
        }
    }

    public Task RouteAsync(ISequencedSessionEvent payload, Func<Task> handler)
    {
        if (payload.SessionSequence <= 0)
        {
            return handler();
        }

        lock (_lock)
        {
            if (_seen.Contains(payload.SessionSequence))
            {
                return Task.CompletedTask;
            }

            if (_buffering)
            {
                _buffer[payload.SessionSequence] = handler;
                return Task.CompletedTask;
            }

            Record(payload.SessionSequence);
        }

        return handler();
    }

    public async Task CompleteAsync(SessionResumeResponseDto response, Func<SessionReplayEventDto, Task> replay)
    {
        foreach (var entry in response.Events.OrderBy(item => item.Sequence))
        {
            if (!TryRecord(entry.Sequence))
            {
                continue;
            }

            await replay(entry).ConfigureAwait(false);
        }

        while (true)
        {
            KeyValuePair<long, Func<Task>>[] pending;
            lock (_lock)
            {
                if (_buffer.Count == 0)
                {
                    _buffering = false;
                    LastSequence = Math.Max(LastSequence, response.ReplayThrough);
                    return;
                }

                pending = _buffer.ToArray();
                _buffer.Clear();
            }

            foreach (var item in pending)
            {
                if (TryRecord(item.Key))
                {
                    await item.Value().ConfigureAwait(false);
                }
            }
        }
    }

    public void AbandonBuffer()
    {
        lock (_lock)
        {
            _buffer.Clear();
            _buffering = false;
        }
    }

    private bool TryRecord(long sequence)
    {
        lock (_lock)
        {
            if (_seen.Contains(sequence))
            {
                return false;
            }

            Record(sequence);
            return true;
        }
    }

    private void Record(long sequence)
    {
        _seen.Add(sequence);
        _recent.Enqueue(sequence);
        LastSequence = Math.Max(LastSequence, sequence);
        while (_recent.Count > RecentCapacity)
        {
            _seen.Remove(_recent.Dequeue());
        }
    }
}
