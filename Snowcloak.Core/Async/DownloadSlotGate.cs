namespace Snowcloak.Core.Async;

public sealed class DownloadSlotGate
{
    private readonly Lock _gate = new();
    private readonly Queue<Waiter> _waiters = new();
    private int _limit;
    private int _inUse;

    public DownloadSlotGate(int limit)
    {
        _limit = Math.Max(1, limit);
    }

    public int Limit
    {
        get
        {
            lock (_gate)
            {
                return _limit;
            }
        }
    }

    public int InUse
    {
        get
        {
            lock (_gate)
            {
                return _inUse;
            }
        }
    }

    public Task WaitAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled(ct);
        }

        lock (_gate)
        {
            if (_inUse < _limit)
            {
                _inUse++;
                return Task.CompletedTask;
            }

            var waiter = new Waiter(ct);
            _waiters.Enqueue(waiter);
            return waiter.Task;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            while (_waiters.TryDequeue(out var waiter))
            {
                if (waiter.TryHandOff())
                {
                    return;
                }
            }

            if (_inUse > 0)
            {
                _inUse--;
            }
        }
    }

    public void UpdateLimit(int newLimit)
    {
        newLimit = Math.Max(1, newLimit);
        lock (_gate)
        {
            _limit = newLimit;
            while (_inUse < _limit && _waiters.TryDequeue(out var waiter))
            {
                if (waiter.TryHandOff())
                {
                    _inUse++;
                }
            }
        }
    }

    private sealed class Waiter
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public Waiter(CancellationToken ct)
        {
            _registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), _tcs);
        }

        public Task Task => _tcs.Task;

        public bool TryHandOff()
        {
            if (_tcs.TrySetResult())
            {
                _registration.Dispose();
                return true;
            }

            return false;
        }
    }
}
