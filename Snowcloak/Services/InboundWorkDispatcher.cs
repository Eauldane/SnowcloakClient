using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Services;

public enum InboundWorkDomain : byte { Presence, Permission, Appearance, Control }
public enum InboundWorkKind : byte { State, Event, Barrier }

public sealed class InboundWorkDispatcher : MediatorSubscriberBase, IHostedService
{
    private const int ShardCount = 8;
    private const int ItemsPerShard = 128;
    private const int ByteQuantum = 4096;
    private const int MaxBytes = 16 * 1024 * 1024;
    private readonly Channel<InboundWorkEnvelope>[] _shards;
    private readonly SemaphoreSlim _itemBudget = new(ShardCount * ItemsPerShard, ShardCount * ItemsPerShard);
    private readonly SemaphoreSlim _byteBudget = new(MaxBytes / ByteQuantum, MaxBytes / ByteQuantum);
    private readonly CancellationTokenSource _stop = new();
    private Task[] _workers = [];
    private long _generation = 1;
    private long _queuedItems;
    private long _queuedBytes;
    private long _oldestTimestamp;

    public InboundWorkDispatcher(ILogger<InboundWorkDispatcher> logger, SnowMediator mediator) : base(logger, mediator)
    {
        _shards = Enumerable.Range(0, ShardCount).Select(_ => Channel.CreateBounded<InboundWorkEnvelope>(
            new BoundedChannelOptions(ItemsPerShard)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            })).ToArray();
        Mediator.Subscribe<ConnectedMessage>(this, _ => AdvanceGeneration());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => AdvanceGeneration());
    }

    public long Generation => Volatile.Read(ref _generation);
    public long QueuedItems => Interlocked.Read(ref _queuedItems);
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    public TimeSpan OldestAge => Volatile.Read(ref _oldestTimestamp) is var timestamp && timestamp != 0
        ? Stopwatch.GetElapsedTime(timestamp) : TimeSpan.Zero;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workers = _shards.Select((channel, index) => Task.Run(() => RunShardAsync(index, channel.Reader, _stop.Token),
            CancellationToken.None)).ToArray();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var shard in _shards)
            shard.Writer.TryComplete();
        try { await Task.WhenAll(_workers).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        Mediator.UnsubscribeAll(this);
    }

    public Task EnqueueAsync(string identity, InboundWorkDomain domain, InboundWorkKind kind,
        int estimatedBytes, Func<Task> handler, long? sequence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(handler);
        var generation = Generation;
        return EnqueueCoreAsync(new InboundWorkEnvelope(identity, domain, kind, generation, sequence,
            Math.Clamp(estimatedBytes, 1, MaxBytes), handler, Stopwatch.GetTimestamp(),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));
    }

    private async Task EnqueueCoreAsync(InboundWorkEnvelope item)
    {
        var bytePermits = Math.Max(1, (item.EstimatedBytes + ByteQuantum - 1) / ByteQuantum);
        await _itemBudget.WaitAsync(_stop.Token).ConfigureAwait(false);
        var acquiredBytes = 0;
        var accounted = false;
        try
        {
            while (acquiredBytes < bytePermits)
            {
                await _byteBudget.WaitAsync(_stop.Token).ConfigureAwait(false);
                acquiredBytes++;
            }
            Interlocked.Increment(ref _queuedItems);
            Interlocked.Add(ref _queuedBytes, item.EstimatedBytes);
            accounted = true;
            Interlocked.CompareExchange(ref _oldestTimestamp, item.EnqueuedTimestamp, 0);
            await _shards[StableShard(item.Identity)].Writer.WriteAsync(item, _stop.Token).ConfigureAwait(false);
        }
        catch
        {
            if (accounted)
            {
                Interlocked.Decrement(ref _queuedItems);
                Interlocked.Add(ref _queuedBytes, -item.EstimatedBytes);
            }
            _itemBudget.Release();
            if (acquiredBytes > 0) _byteBudget.Release(acquiredBytes);
            throw;
        }
        await item.Completion.Task.ConfigureAwait(false);
    }

    private async Task RunShardAsync(int index, ChannelReader<InboundWorkEnvelope> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (item.Generation != Generation)
                        throw new OperationCanceledException("Inbound work belongs to an expired connection generation.");
                    await item.Handler().ConfigureAwait(false);
                    item.Completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                    Logger.LogWarning(ex, "Inbound {Domain} work failed on shard {Shard} for an identity", item.Domain, index);
                }
                finally
                {
                    Interlocked.Decrement(ref _queuedItems);
                    Interlocked.Add(ref _queuedBytes, -item.EstimatedBytes);
                    _itemBudget.Release();
                    _byteBudget.Release(Math.Max(1, (item.EstimatedBytes + ByteQuantum - 1) / ByteQuantum));
                    if (Interlocked.Read(ref _queuedItems) == 0)
                        Interlocked.Exchange(ref _oldestTimestamp, 0);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void AdvanceGeneration() => Interlocked.Increment(ref _generation);

    private static int StableShard(string identity)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in identity)
                hash = (hash ^ character) * 16777619;
            return (int)(hash % ShardCount);
        }
    }

    private sealed record InboundWorkEnvelope(string Identity, InboundWorkDomain Domain, InboundWorkKind Kind,
        long Generation, long? Sequence, int EstimatedBytes, Func<Task> Handler, long EnqueuedTimestamp,
        TaskCompletionSource Completion);
}
