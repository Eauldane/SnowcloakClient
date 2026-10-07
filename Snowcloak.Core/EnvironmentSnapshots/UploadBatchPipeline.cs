namespace Snowcloak.Core.EnvironmentSnapshots;

public static class UploadBatchPipeline
{
    public static async Task RunAsync<T>(IReadOnlyList<T> items,
        Func<T, CancellationToken, Task<Stream>> prepare,
        Func<T, Stream, CancellationToken, Task> upload,
        CancellationToken ct, bool prefetch = true)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = operation.Token;
        Task current = Task.CompletedTask;
        try
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (!prefetch) await current.ConfigureAwait(false);
                Stream? pending = await prepare(item, ct).ConfigureAwait(false);
                try
                {
                    await current.ConfigureAwait(false);
                    current = SendAsync(item, pending, upload, operation);
                    pending = null;
                }
                finally { if (pending != null) await pending.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally { await current.ConfigureAwait(false); }
    }
    private static async Task SendAsync<T>(T item, Stream stream,
        Func<T, Stream, CancellationToken, Task> upload, CancellationTokenSource operation)
    {
        try { await upload(item, stream, operation.Token).ConfigureAwait(false); }
        catch { operation.Cancel(); throw; }
        finally { await stream.DisposeAsync().ConfigureAwait(false); }
    }
}
