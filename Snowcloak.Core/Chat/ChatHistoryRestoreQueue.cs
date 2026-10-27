namespace Snowcloak.Core.Chat;

public static class ChatHistoryRestoreQueue
{
    public const int MaxConcurrency = 3;

    public static async Task RunAsync<T>(IReadOnlyList<T> candidates, Func<T, ConversationKey> key,
        Func<ConversationKey?> activeConversation, Func<T, CancellationToken, Task> restore,
        CancellationToken cancellationToken)
    {
        var pending = candidates.ToList();
        var gate = new Lock();
        await Task.WhenAll(Enumerable.Range(0, Math.Min(MaxConcurrency, pending.Count)).Select(_ => WorkerAsync())).ConfigureAwait(false);

        async Task WorkerAsync()
        {
            while (true)
            {
                T candidate;
                lock (gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pending.Count == 0) return;
                    var active = activeConversation();
                    var index = active.HasValue ? pending.FindIndex(item => key(item) == active.Value) : -1;
                    if (index < 0) index = 0;
                    candidate = pending[index];
                    pending.RemoveAt(index);
                }
                await restore(candidate, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
