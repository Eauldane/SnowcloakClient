namespace Snowcloak.Core.Scheduling;

public sealed class FrameSchedulePlanner
{
    private sealed class Entry
    {
        public required int Id { get; init; }
        public required TickInterval Interval { get; set; }
        public required TickPriority Priority { get; set; }
        public required long Sequence { get; init; }
        public long LastRanFrame { get; set; } = TickInterval.NeverRanFrame;
        public double LastRanMs { get; set; } = TickInterval.NeverRanMs;
        public bool Paused { get; set; }
    }

    private readonly Dictionary<int, Entry> _entries = new();
    private readonly List<DueTicker> _dueScratch = new();
    private readonly Comparison<DueTicker> _dueComparison;
    private int _nextId = 1;
    private long _nextSequence;

    public FrameSchedulePlanner()
    {
        _dueComparison = CompareDue;
    }

    public int Count => _entries.Count;

    public int Register(TickInterval interval, TickPriority priority)
    {
        var id = _nextId++;
        _entries[id] = new Entry
        {
            Id = id,
            Interval = interval,
            Priority = priority,
            Sequence = _nextSequence++,
        };
        return id;
    }

    public bool Unregister(int id) => _entries.Remove(id);

    public bool Contains(int id) => _entries.ContainsKey(id);

    public void SetPaused(int id, bool paused)
    {
        if (_entries.TryGetValue(id, out var entry))
            entry.Paused = paused;
    }

    public bool TryGetPriority(int id, out TickPriority priority)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            priority = entry.Priority;
            return true;
        }

        priority = default;
        return false;
    }

    public void MarkRan(int id, long frame, double nowMs)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.LastRanFrame = frame;
            entry.LastRanMs = nowMs;
        }
    }

    public IReadOnlyList<DueTicker> CollectDue(long frame, double nowMs)
    {
        _dueScratch.Clear();
        foreach (var entry in _entries.Values)
        {
            if (entry.Paused)
                continue;
            if (entry.Interval.IsDue(frame, nowMs, entry.LastRanFrame, entry.LastRanMs))
                _dueScratch.Add(new DueTicker(entry.Id, entry.Priority));
        }

        _dueScratch.Sort(_dueComparison);
        return _dueScratch;
    }

    private int CompareDue(DueTicker a, DueTicker b)
    {
        var byPriority = ((int)a.Priority).CompareTo((int)b.Priority);
        if (byPriority != 0)
            return byPriority;
        return _entries[a.Id].Sequence.CompareTo(_entries[b.Id].Sequence);
    }
}
