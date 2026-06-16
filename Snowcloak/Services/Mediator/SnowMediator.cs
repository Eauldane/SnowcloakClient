using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace Snowcloak.Services.Mediator;

/// <summary>
/// In-process message bus. Subscribers register typed handlers; publishers send
/// <see cref="MessageBase"/> records that are dispatched to matching subscribers.
///
/// Dispatch is reflection-free: each subscription stores a closure that casts the message
/// to its concrete type once, at subscribe time. Queued (non-same-thread) messages flow
/// through an unbounded <see cref="Channel{T}"/> drained by a single reader, replacing the
/// older polling loop. Same-thread messages (<see cref="MessageBase.KeepThreadContext"/>)
/// run synchronously on the publishing thread, preserving framework-thread affinity.
///
/// Each subscription carries a <see cref="MediatorPriority"/>; for a given message, subscribers
/// are invoked in priority order (highest first), and in subscribe order within a priority.
/// </summary>
public sealed class SnowMediator : IHostedService, IDisposable
{
    private readonly ILogger<SnowMediator> _logger;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly SnowcloakConfigService _snowcloakConfigService;

    private readonly Lock _subscriberLock = new();
    private readonly ConcurrentDictionary<(Type Type, string? Key), SubscriberSet> _subscribers = new();
    private readonly ConcurrentDictionary<Subscription, DateTime> _lastErrorTime = new();

    private readonly Channel<MessageBase> _queue = Channel.CreateUnbounded<MessageBase>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _loopCts = new();
    private Task? _queueTask;
    private int _processingStarted;

    public SnowMediator(ILogger<SnowMediator> logger, PerformanceCollectorService performanceCollector, SnowcloakConfigService snowcloakConfigService)
    {
        _logger = logger;
        _performanceCollector = performanceCollector;
        _snowcloakConfigService = snowcloakConfigService;
    }

    public void Publish<T>(T message) where T : MessageBase
    {
        if (message.KeepThreadContext)
        {
            // Run synchronously on the calling thread (e.g. the framework thread).
            ExecuteMessage(message);
        }
        else
        {
            _queue.Writer.TryWrite(message);
        }
    }

    public void Subscribe<T>(IMediatorSubscriber subscriber, Action<T> action, MediatorPriority priority = MediatorPriority.Normal) where T : MessageBase
        => AddSubscription(typeof(T), key: null, new Subscription(subscriber, message => action((T)message), invokeAsync: null, priority));

    public void Subscribe<T>(IMediatorSubscriber subscriber, Func<T, Task> asyncAction, MediatorPriority priority = MediatorPriority.Normal) where T : MessageBase
        => AddSubscription(typeof(T), key: null, CreateAsyncSubscription(subscriber, asyncAction, priority));

    public void SubscribeKeyed<T>(IMediatorSubscriber subscriber, string key, Action<T> action, MediatorPriority priority = MediatorPriority.Normal) where T : MessageBase
        => AddSubscription(typeof(T), key, new Subscription(subscriber, message => action((T)message), invokeAsync: null, priority));

    /// <inheritdoc cref="Subscribe{T}(IMediatorSubscriber, Func{T, Task}, MediatorPriority)"/>
    public void SubscribeKeyed<T>(IMediatorSubscriber subscriber, string key, Func<T, Task> asyncAction, MediatorPriority priority = MediatorPriority.Normal) where T : MessageBase
        => AddSubscription(typeof(T), key, CreateAsyncSubscription(subscriber, asyncAction, priority));

    private Subscription CreateAsyncSubscription<T>(IMediatorSubscriber subscriber, Func<T, Task> asyncAction, MediatorPriority priority) where T : MessageBase
    {
        Func<MessageBase, Task> invokeAsync = message => asyncAction((T)message);
        // Synchronous fallback for the same-thread dispatch path: start the handler and observe it
        // detached so an exception is still logged. The queued path awaits InvokeAsync directly.
        return new Subscription(subscriber, message => ObserveDetached(invokeAsync(message), subscriber, typeof(T)), invokeAsync, priority);
    }

    private void AddSubscription(Type messageType, string? key, Subscription subscription)
    {
        lock (_subscriberLock)
        {
            var set = _subscribers.GetOrAdd((messageType, key), static _ => new SubscriberSet());
            set.Add(subscription);
        }

        _logger.LogTrace("Subscriber added for message {message}{key}: {sub}",
            messageType.Name, key is null ? string.Empty : $":{key}", subscription.Subscriber.GetType().Name);
    }

    private void ObserveDetached(Task task, IMediatorSubscriber subscriber, Type messageType)
    {
        if (task.IsCompletedSuccessfully)
            return;

        _ = task.ContinueWith(
            t => _logger.LogError(t.Exception?.InnerException ?? t.Exception, "Async handler for {sub} failed handling {msg}",
                subscriber.GetType().Name, messageType.Name),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public void Unsubscribe<T>(IMediatorSubscriber subscriber) where T : MessageBase
    {
        lock (_subscriberLock)
        {
            if (_subscribers.TryGetValue((typeof(T), null), out var set))
                set.RemoveBySubscriber(subscriber);
        }
    }

    internal void UnsubscribeAll(IMediatorSubscriber subscriber)
    {
        lock (_subscriberLock)
        {
            foreach (var (key, set) in _subscribers)
            {
                if (set.RemoveBySubscriber(subscriber) > 0)
                    _logger.LogDebug("{sub} unsubscribed from {msg}", subscriber.GetType().Name, key.Type.Name);
            }
        }
    }

    private void ExecuteMessage(MessageBase message)
    {
        if (!_subscribers.TryGetValue((message.GetType(), message.SubscriberKey), out var set))
            return;

        // Lock-free read of an immutable, priority-ordered snapshot. A concurrent
        // subscribe/unsubscribe swaps in a new array, leaving this one untouched.
        var snapshot = set.Snapshot;
        if (snapshot.Length == 0)
            return;

        bool logPerformance = _snowcloakConfigService.Current.LogPerformance;

        foreach (var subscription in snapshot)
        {
            try
            {
                if (logPerformance)
                {
                    var sameThread = message.KeepThreadContext ? "$" : string.Empty;
                    _performanceCollector.LogPerformance(this,
                        $"{sameThread}Execute>{message.GetType().Name}+{subscription.Subscriber.GetType().Name}>{subscription.Subscriber}",
                        () => subscription.Invoke(message));
                }
                else
                {
                    subscription.Invoke(message);
                }
            }
            catch (Exception ex)
            {
                // Throttle repeated errors from the same subscription to once per 10s.
                if (_lastErrorTime.TryGetValue(subscription, out var lastErrorTime) && lastErrorTime.Add(TimeSpan.FromSeconds(10)) > DateTime.UtcNow)
                    continue;

                _logger.LogError(ex.InnerException ?? ex, "Error executing {type} for subscriber {subscriber}",
                    message.GetType().Name, subscription.Subscriber.GetType().Name);
                _lastErrorTime[subscription] = DateTime.UtcNow;
            }
        }
    }
    
    private async Task ExecuteMessageAsync(MessageBase message)
    {
        if (!_subscribers.TryGetValue((message.GetType(), message.SubscriberKey), out var set))
            return;

        var snapshot = set.Snapshot;
        if (snapshot.Length == 0)
            return;

        bool logPerformance = _snowcloakConfigService.Current.LogPerformance;

        foreach (var subscription in snapshot)
        {
            try
            {
                if (subscription.InvokeAsync != null)
                {
                    // Awaited so subsequent queued messages stay ordered (perf logging is sync-only).
                    await subscription.InvokeAsync(message).ConfigureAwait(false);
                }
                else if (logPerformance)
                {
                    var sameThread = message.KeepThreadContext ? "$" : string.Empty;
                    _performanceCollector.LogPerformance(this,
                        $"{sameThread}Execute>{message.GetType().Name}+{subscription.Subscriber.GetType().Name}>{subscription.Subscriber}",
                        () => subscription.Invoke(message));
                }
                else
                {
                    subscription.Invoke(message);
                }
            }
            catch (Exception ex)
            {
                // Throttle repeated errors from the same subscription to once per 10s
                if (_lastErrorTime.TryGetValue(subscription, out var lastErrorTime) && lastErrorTime.Add(TimeSpan.FromSeconds(10)) > DateTime.UtcNow)
                    continue;

                _logger.LogError(ex.InnerException ?? ex, "Error executing {type} for subscriber {subscriber}",
                    message.GetType().Name, subscription.Subscriber.GetType().Name);
                _lastErrorTime[subscription] = DateTime.UtcNow;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Started SnowMediator");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        await _loopCts.CancelAsync().ConfigureAwait(false);

        if (_queueTask != null)
        {
            try
            {
                await _queueTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_loopCts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                // expected during shutdown
            }
        }
    }

    public void Dispose()
    {
        _loopCts.Dispose();
    }

    /// <summary>
    /// Begins draining queued messages. Messages published before this is called are buffered
    /// in the channel and delivered once processing starts. Idempotent.
    /// </summary>
    public void StartQueueProcessing()
    {
        if (Interlocked.Exchange(ref _processingStarted, 1) != 0)
            return;

        _logger.LogInformation("Starting Message Queue Processing");
        _queueTask = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(_loopCts.Token).ConfigureAwait(false))
            {
                await ExecuteMessageAsync(message).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected during shutdown
        }
    }

    public void PrintSubscriberInfo()
    {
        foreach (var subscriber in _subscribers.SelectMany(c => c.Value.Snapshot.Select(v => v.Subscriber))
            .DistinctBy(p => p).OrderBy(p => p.GetType().FullName, StringComparer.Ordinal).ToList())
        {
            _logger.LogInformation("Subscriber {type}: {sub}", subscriber.GetType().Name, subscriber.ToString());
            StringBuilder sb = new();
            sb.Append("=> ");
            foreach (var item in _subscribers.Where(item => item.Value.Snapshot.Any(v => v.Subscriber == subscriber)).ToList())
            {
                sb.Append(item.Key.Type.Name);
                if (item.Key.Key != null)
                    sb.Append($":{item.Key.Key}");
                sb.Append(", ");
            }

            if (!string.Equals(sb.ToString(), "=> ", StringComparison.Ordinal))
                _logger.LogInformation("{sb}", sb.ToString());
            _logger.LogInformation("---");
        }
    }

    /// <summary>
    /// Subscriptions for a single (message type, key). Maintains an immutable, priority-ordered
    /// snapshot that dispatch reads without locking; the mutable list is only touched under the
    /// mediator's subscriber lock.
    /// </summary>
    private sealed class SubscriberSet
    {
        private readonly List<Subscription> _items = [];
        private Subscription[] _snapshot = [];

        public Subscription[] Snapshot => Volatile.Read(ref _snapshot);

        public void Add(Subscription subscription)
        {
            _items.Add(subscription);
            Rebuild();
        }

        public int RemoveBySubscriber(IMediatorSubscriber subscriber)
        {
            int removed = _items.RemoveAll(s => s.Subscriber == subscriber);
            if (removed > 0)
                Rebuild();
            return removed;
        }

        private void Rebuild()
        {
            // Highest priority first; OrderBy is stable, so subscribe order is kept within each tier.
            Volatile.Write(ref _snapshot, _items.OrderBy(static s => s.Priority).ToArray());
        }
    }

    private sealed class Subscription
    {
        public Subscription(IMediatorSubscriber subscriber, Action<MessageBase> invoke, Func<MessageBase, Task>? invokeAsync, MediatorPriority priority)
        {
            Subscriber = subscriber;
            Invoke = invoke;
            InvokeAsync = invokeAsync;
            Priority = priority;
        }

        public IMediatorSubscriber Subscriber { get; }
        
        public Action<MessageBase> Invoke { get; }

        public Func<MessageBase, Task>? InvokeAsync { get; }

        public MediatorPriority Priority { get; }
    }
}
