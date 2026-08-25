using Snowcloak.API.Dto.Session;

namespace Snowcloak.WebAPI.SignalR;

internal sealed class SessionResumeState
{
    private const int RecentCapacity = 256;
    private readonly SortedDictionary<long, Func<Task>> _buffer = [];
    private readonly Dictionary<long, Task> _inFlight = [];
    private readonly Queue<long> _recent = [];
    private readonly HashSet<long> _seen = [];
    private readonly Lock _lock = new();
    private bool _buffering;
    private long? _pendingSnapshotSequence;
    private int _generation;

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

            ResetState(sessionId);
        }
    }

    public void BeginFullResync(string sessionId, long replayThrough)
    {
        lock (_lock)
        {
            ResetState(sessionId);
            _pendingSnapshotSequence = Math.Max(0, replayThrough);
        }
    }

    public void CommitFullResync()
    {
        lock (_lock)
        {
            if (_pendingSnapshotSequence.HasValue)
            {
                LastSequence = Math.Max(LastSequence, _pendingSnapshotSequence.Value);
                _pendingSnapshotSequence = null;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            ResetState(string.Empty);
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

        TaskCompletionSource completion;
        int generation;
        lock (_lock)
        {
            if (_seen.Contains(payload.SessionSequence))
            {
                return Task.CompletedTask;
            }

            if (_buffering)
            {
                _buffer.TryAdd(payload.SessionSequence, handler);
                return Task.CompletedTask;
            }

            if (_inFlight.TryGetValue(payload.SessionSequence, out Task? existing))
            {
                return existing;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight[payload.SessionSequence] = completion.Task;
            generation = _generation;
        }

        _ = ExecuteAndRecordAsync(payload.SessionSequence, handler, generation, completion);
        return completion.Task;
    }

    public async Task CompleteAsync(SessionResumeResponseDto response, Func<SessionReplayEventDto, Task> replay)
    {
        foreach (var entry in response.Events.OrderBy(item => item.Sequence))
        {
            lock (_lock)
            {
                if (_seen.Contains(entry.Sequence))
                {
                    continue;
                }
            }

            await replay(entry).ConfigureAwait(false);
            lock (_lock)
            {
                if (!_seen.Contains(entry.Sequence))
                {
                    Record(entry.Sequence);
                }
            }
        }

        while (true)
        {
            KeyValuePair<long, Func<Task>> pending;
            lock (_lock)
            {
                if (_buffer.Count == 0)
                {
                    _buffering = false;
                    LastSequence = Math.Max(LastSequence, response.ReplayThrough);
                    return;
                }

                pending = _buffer.First();
                if (_seen.Contains(pending.Key))
                {
                    _buffer.Remove(pending.Key);
                    continue;
                }
            }

            await pending.Value().ConfigureAwait(false);
            lock (_lock)
            {
                if (!_seen.Contains(pending.Key))
                {
                    Record(pending.Key);
                }
                _buffer.Remove(pending.Key);
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

    private async Task ExecuteAndRecordAsync(
        long sequence,
        Func<Task> handler,
        int generation,
        TaskCompletionSource completion)
    {
        try
        {
            await handler().ConfigureAwait(false);
            lock (_lock)
            {
                _inFlight.Remove(sequence);
                if (generation == _generation && !_seen.Contains(sequence))
                {
                    Record(sequence);
                }
            }
            completion.SetResult();
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _inFlight.Remove(sequence);
            }
            completion.SetException(ex);
        }
    }

    private void ResetState(string sessionId)
    {
        _generation++;
        SessionId = sessionId;
        LastSequence = 0;
        _pendingSnapshotSequence = null;
        _buffer.Clear();
        _inFlight.Clear();
        _recent.Clear();
        _seen.Clear();
        _buffering = false;
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
