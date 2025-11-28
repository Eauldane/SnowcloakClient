using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.SnowcloakConfiguration;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace Snowcloak.Services.Mediator;

public sealed class SnowMediator : IHostedService
{
    private readonly Lock _addRemoveLock = new();
    private readonly ConcurrentDictionary<SubscriberAction, DateTime> _lastErrorTime = [];
    private readonly ILogger<SnowMediator> _logger;
    private readonly CancellationTokenSource _loopCts = new();
    private readonly ConcurrentQueue<MessageBase> _messageQueue = new();
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly ConcurrentDictionary<(Type, string?), HashSet<SubscriberAction>> _subscriberDict = [];
    private bool _processQueue = false;
    private readonly ConcurrentDictionary<(Type, string?), MethodInfo?> _genericExecuteMethods = new();
    public SnowMediator(ILogger<SnowMediator> logger, PerformanceCollectorService performanceCollector, SnowcloakConfigService snowcloakConfigService)
    {
        _logger = logger;
        _performanceCollector = performanceCollector;
        _snowcloakConfigService = snowcloakConfigService;
    }

    public void PrintSubscriberInfo()
    {
        foreach (var subscriber in _subscriberDict.SelectMany(c => c.Value.Select(v => v.Subscriber))
            .DistinctBy(p => p).OrderBy(p => p.GetType().FullName, StringComparer.Ordinal).ToList())
        {
            _logger.LogInformation("Subscriber {type}: {sub}", subscriber.GetType().Name, subscriber.ToString());
            StringBuilder sb = new();
            sb.Append("=> ");
            foreach (var item in _subscriberDict.Where(item => item.Value.Any(v => v.Subscriber == subscriber)).ToList())
            {
                sb.Append(item.Key.Item1.Name);
                if (item.Key.Item2 != null)
                    sb.Append($":{item.Key.Item2!}");
                sb.Append(", ");
            }

            if (!string.Equals(sb.ToString(), "=> ", StringComparison.Ordinal))
                _logger.LogInformation("{sb}", sb.ToString());
            _logger.LogInformation("---");
        }
    }

    public void Publish<T>(T message) where T : MessageBase
    {
        if (message.KeepThreadContext)
        {
            ExecuteMessage(message);
        }
        else
        {
            _messageQueue.Enqueue(message);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SnowSnowMediator");

        _ = Task.Run(async () =>
        {
            while (!_loopCts.Token.IsCancellationRequested)
            {
                while (!_processQueue)
                {
                    await Task.Delay(100, _loopCts.Token).ConfigureAwait(false);
                }

                await Task.Delay(100, _loopCts.Token).ConfigureAwait(false);

                while (_messageQueue.TryDequeue(out var message))
                {
                    ExecuteMessage(message);
                }
            }
        });

        _logger.LogInformation("Started SnowMediator");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _messageQueue.Clear();
        _loopCts.Cancel();
        return Task.CompletedTask;
    }

    public void Subscribe<T>(IMediatorSubscriber subscriber, Action<T> action) where T : MessageBase
    {
        lock (_addRemoveLock)
        {
            _subscriberDict.TryAdd((typeof(T), null), []);

            if (!_subscriberDict[(typeof(T), null)].Add(new(subscriber, action)))
            {
                throw new InvalidOperationException("Already subscribed");
            }

            _logger.LogTrace("Subscriber added for message {message}: {sub}", typeof(T).Name, subscriber.GetType().Name);
        }
    }

    public void SubscribeKeyed<T>(IMediatorSubscriber subscriber, string key, Action<T> action) where T : MessageBase
    {
        lock (_addRemoveLock)
        {
            _subscriberDict.TryAdd((typeof(T), key), []);

            if (!_subscriberDict[(typeof(T), key)].Add(new(subscriber, action)))
            {
                throw new InvalidOperationException("Already subscribed");
            }

            _logger.LogTrace("Subscriber added for message {message}:{key}: {sub}", typeof(T).Name, key, subscriber.GetType().Name);
        }
    }

    public void Unsubscribe<T>(IMediatorSubscriber subscriber) where T : MessageBase
    {
        lock (_addRemoveLock)
        {
            if (_subscriberDict.ContainsKey((typeof(T), null)))
            {
                _subscriberDict[(typeof(T), null)].RemoveWhere(p => p.Subscriber == subscriber);
            }
        }
    }

    internal void UnsubscribeAll(IMediatorSubscriber subscriber)
    {
        lock (_addRemoveLock)
        {
            foreach (var kvp in _subscriberDict.Select(k => k.Key))
            {
                int unSubbed = _subscriberDict[kvp]?.RemoveWhere(p => p.Subscriber == subscriber) ?? 0;
                if (unSubbed > 0)
                {
                    _logger.LogDebug("{sub} unsubscribed from {msg}", subscriber.GetType().Name, kvp.Item1.Name);
                }
            }
        }
    }

    private void ExecuteMessage(MessageBase message)
    {
        if (!_subscriberDict.TryGetValue((message.GetType(), message.SubscriberKey), out HashSet<SubscriberAction>? subscribers) || subscribers == null || !subscribers.Any()) return;

        List<SubscriberAction> subscribersCopy = [];
        lock (_addRemoveLock)
        {
            subscribersCopy = subscribers?.Where(s => s.Subscriber != null).ToList() ?? [];
        }

#pragma warning disable S3011 // Reflection should not be used to increase accessibility of classes, methods, or fields
        var msgType = message.GetType();
        if (!_genericExecuteMethods.TryGetValue((msgType, message.SubscriberKey), out var methodInfo))
        {
            _genericExecuteMethods[(msgType, message.SubscriberKey)] = methodInfo = GetType()
                 .GetMethod(nameof(ExecuteReflected), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                 .MakeGenericMethod(msgType);
        }

        methodInfo!.Invoke(this, [subscribersCopy, message]);
#pragma warning restore S3011 // Reflection should not be used to increase accessibility of classes, methods, or fields
    }

    private void ExecuteReflected<T>(List<SubscriberAction> subscribers, T message) where T : MessageBase
    {
        foreach (SubscriberAction subscriber in subscribers)
        {
            try
            {
                if (_snowcloakConfigService.Current.LogPerformance)
                {
                    var isSameThread = message.KeepThreadContext ? "$" : string.Empty;
                    _performanceCollector.LogPerformance(this, $"{isSameThread}Execute>{message.GetType().Name}+{subscriber.Subscriber.GetType().Name}>{subscriber.Subscriber}",
                        () => ((Action<T>)subscriber.Action).Invoke(message));
                }
                else
                {
                    ((Action<T>)subscriber.Action).Invoke(message);
                }
            }
            catch (Exception ex)
            {
                if (_lastErrorTime.TryGetValue(subscriber, out var lastErrorTime) && lastErrorTime.Add(TimeSpan.FromSeconds(10)) > DateTime.UtcNow)
                    continue;

                _logger.LogError(ex.InnerException ?? ex, "Error executing {type} for subscriber {subscriber}",
                    message.GetType().Name, subscriber.Subscriber.GetType().Name);
                _lastErrorTime[subscriber] = DateTime.UtcNow;
            }
        }
    }

    public void StartQueueProcessing()
    {
        _logger.LogInformation("Starting Message Queue Processing");
        _processQueue = true;
    }

    private sealed class SubscriberAction
    {
        public SubscriberAction(IMediatorSubscriber subscriber, object action)
        {
            Subscriber = subscriber;
            Action = action;
        }

        public object Action { get; }
        public IMediatorSubscriber Subscriber { get; }
    }
}