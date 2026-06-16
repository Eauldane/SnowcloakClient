namespace Snowcloak.FileCache;

internal sealed class CacheScanPauseGate
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _holds = new(StringComparer.Ordinal);

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                foreach (var count in _holds.Values)
                {
                    if (count > 0) return true;
                }

                return false;
            }
        }
    }

    public void Hold(string source)
    {
        lock (_lock)
        {
            _holds.TryGetValue(source, out var count);
            _holds[source] = count + 1;
        }
    }

    public void Release(string source)
    {
        lock (_lock)
        {
            if (!_holds.TryGetValue(source, out var count)) return;
            if (count <= 1)
                _holds.Remove(source);
            else
                _holds[source] = count - 1;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _holds.Clear();
        }
    }

    public string Describe()
    {
        lock (_lock)
        {
            return string.Join(", ", _holds.Where(h => h.Value > 0).Select(h => h.Key + ": " + h.Value));
        }
    }

    public IDisposable Pause(string source)
    {
        Hold(source);
        return new Releaser(this, source);
    }

    private sealed class Releaser(CacheScanPauseGate gate, string source) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            gate.Release(source);
        }
    }
}
