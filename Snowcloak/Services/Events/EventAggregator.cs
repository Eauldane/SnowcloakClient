using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using System.Threading.Channels;

namespace Snowcloak.Services.Events;

public sealed partial class EventAggregator : MediatorSubscriberBase, IHostedService, IDisposable
{
    private const int MaxLogFiles = 10;
    private const int MaxLogBatchSize = 128;

    private readonly RollingList<Event> _events = new(500);
    private readonly SemaphoreSlim _lock = new(1);
    private readonly Channel<EventLogEntry> _logQueue = Channel.CreateUnbounded<EventLogEntry>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly string _configDirectory;
    private DateOnly _lastRotationDate = DateOnly.MinValue;
    private Task? _logWriterTask;

    public Lazy<List<Event>> EventList { get; private set; }
    public bool NewEventsAvailable => !EventList.IsValueCreated;
    public string EventLogFolder => Path.Combine(_configDirectory, "eventlog");

    public EventAggregator(SnowcloakConfigService configService, ILogger<EventAggregator> logger, SnowMediator snowMediator)
        : base(logger, snowMediator)
    {
        ArgumentNullException.ThrowIfNull(configService);

        Mediator.Subscribe<EventMessage>(this, (msg) =>
        {
            _lock.Wait();
            try
            {
                LogReceivedEvent(Logger, msg.Event);
                _events.Add(msg.Event);
                if (configService.Current.LogEvents)
                    QueueFileWrite(msg.Event);
            }
            finally
            {
                _lock.Release();
            }

            RecreateLazy();
        });

        EventList = CreateEventLazy();
        _configDirectory = configService.ConfigurationDirectory;
    }

    private void RecreateLazy()
    {
        if (!EventList.IsValueCreated) return;

        EventList = CreateEventLazy();
    }

    private Lazy<List<Event>> CreateEventLazy()
    {
        return new Lazy<List<Event>>(() =>
        {
            _lock.Wait();
            try
            {
                return [.. _events];
            }
            finally
            {
                _lock.Release();
            }
        });
    }

    private void QueueFileWrite(Event receivedEvent)
    {
        _logQueue.Writer.TryWrite(new EventLogEntry(receivedEvent.EventTime, receivedEvent.ToString()));
    }

    private async Task ProcessLogQueueAsync()
    {
        var pending = new List<EventLogEntry>(MaxLogBatchSize);

        await foreach (var entry in _logQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            pending.Add(entry);
            while (pending.Count < MaxLogBatchSize && _logQueue.Reader.TryRead(out var next))
            {
                pending.Add(next);
            }

            FlushLogEntries(pending);
        }

        while (_logQueue.Reader.TryRead(out var remaining))
        {
            pending.Add(remaining);
        }

        FlushLogEntries(pending);
    }

    private void FlushLogEntries(List<EventLogEntry> entries)
    {
        if (entries.Count == 0)
            return;

        try
        {
            Directory.CreateDirectory(EventLogFolder);
            foreach (var group in entries.GroupBy(entry => DateOnly.FromDateTime(entry.EventTime)))
            {
                var eventLogFile = Path.Combine(EventLogFolder, $"{group.Key:yyyy-MM-dd}-events.log");
                File.AppendAllLines(eventLogFile, group.Select(entry => entry.Line));
                RotateLogFilesIfNeeded(group.Key);
            }
        }
        catch (IOException ex)
        {
            LogEventFileWriteFailed(Logger, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogEventFileWriteFailed(Logger, ex);
        }
        finally
        {
            entries.Clear();
        }
    }

    private void RotateLogFilesIfNeeded(DateOnly logDate)
    {
        if (logDate == _lastRotationDate)
            return;

        _lastRotationDate = logDate;
        try
        {
            var files = Directory.EnumerateFiles(EventLogFolder, "*.log")
                .Select(file => new FileInfo(file))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();

            while (files.Count > MaxLogFiles)
            {
                files[0].Delete();
                files.RemoveAt(0);
            }
        }
        catch (IOException ex)
        {
            LogEventLogDeleteFailed(Logger, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogEventLogDeleteFailed(Logger, ex);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogStarting(Logger);
        _logWriterTask = Task.Run(ProcessLogQueueAsync, cancellationToken);
        LogStarted(Logger);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeAll();
        _logQueue.Writer.TryComplete();
        if (_logWriterTask != null)
            await _logWriterTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    private sealed record EventLogEntry(DateTime EventTime, string Line);

    [LoggerMessage(EventId = 0, Level = LogLevel.Trace, Message = "Received Event: {ReceivedEvent}")]
    private static partial void LogReceivedEvent(ILogger logger, Event receivedEvent);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Could not write to event file")]
    private static partial void LogEventFileWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Could not delete old event logs")]
    private static partial void LogEventLogDeleteFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Starting EventAggregatorService")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Started EventAggregatorService")]
    private static partial void LogStarted(ILogger logger);
}
