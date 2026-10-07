namespace Snowcloak.Core.EnvironmentSnapshots;

/// <summary>Process-wide restore admission; operations already admitted are cancelled and drained.</summary>
public static class SnapshotExclusion
{
    private static readonly object Gate = new();
    private static CancellationTokenSource _generation = new();
    private static int _active;
    private static bool _exclusive;
    private static readonly AsyncLocal<bool> RestoreMutation = new();
    public static IDisposable EnterRestoreMutation() { if (!Blocked) throw new InvalidOperationException("Restore lease required."); var before = RestoreMutation.Value; RestoreMutation.Value = true; return new Mutation(before); }
    private sealed class Mutation(bool before) : IDisposable { public void Dispose() => RestoreMutation.Value = before; }
    public static bool Blocked { get { lock (Gate) return _exclusive; } }
    public sealed class Work : IDisposable
    {
        private CancellationTokenSource? _source;
        public CancellationToken Token => _source!.Token;
        internal Work(CancellationTokenSource source) => _source = source;
        public void Dispose() { lock (Gate) { if (_source == null) return; _source.Dispose(); _source = null; _active--; } }
    }
    public static Work Enter(CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (_exclusive && !RestoreMutation.Value) throw new OperationCanceledException("Snowcloak restore is holding exclusive admission.", ct);
            _active++; return new Work(CancellationTokenSource.CreateLinkedTokenSource(ct, RestoreMutation.Value ? CancellationToken.None : _generation.Token));
        }
    }
    public static async Task<Work> EnterWhenAvailableAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (!_exclusive || RestoreMutation.Value)
                {
                    _active++;
                    return new Work(CancellationTokenSource.CreateLinkedTokenSource(ct,
                        RestoreMutation.Value ? CancellationToken.None : _generation.Token));
                }
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }
    public static async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        CancellationTokenSource generation;
        lock (Gate) { if (_exclusive) throw new InvalidOperationException("A restore or recovery is already active."); _exclusive = true; generation = _generation; }
        generation.Cancel();
        try
        {
            while (true) { ct.ThrowIfCancellationRequested(); lock (Gate) { if (_active == 0) return new Exclusive(); } await Task.Delay(50, ct).ConfigureAwait(false); }
        }
        catch { new Exclusive().Dispose(); throw; }
    }
    private sealed class Exclusive : IDisposable
    {
        private bool _disposed;
        public void Dispose() { lock (Gate) { if (_disposed) return; _disposed = true; _generation.Dispose(); _generation = new(); _exclusive = false; } }
    }
}
