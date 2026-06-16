using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Utils;

namespace Snowcloak.FileCache;

public sealed class TransientRecordingService : IDisposable, IAsyncDisposable
{
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly ILogger<TransientRecordingService> _logger;
    private readonly List<TransientRecord> _recordedTransients = [];
    private readonly IReadOnlyList<TransientRecord> _recordedTransientView;
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly Lock _recordingLock = new();
    private int _disposed;

    public TransientRecordingService(ILogger<TransientRecordingService> logger)
    {
        _logger = logger;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _recordedTransientView = _recordedTransients.AsReadOnly();
    }

    public bool IsRecording { get; private set; }
    public IReadOnlyList<TransientRecord> RecordedTransients => _recordedTransientView;
    public ValueProgress<TimeSpan> TimeRemaining { get; } = new();

    public void StartRecording(CancellationToken token)
    {
        if (Volatile.Read(ref _disposed) != 0 || IsRecording || _backgroundTasks.IsStopping || _runtimeCts.IsCancellationRequested)
        {
            return;
        }

        lock (_recordingLock)
        {
            _recordedTransients.Clear();
            IsRecording = true;
        }

        TimeRemaining.Report(TimeSpan.FromSeconds(150));
        _ = _backgroundTasks.Run(async () =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, _runtimeCts.Token);
            var recordingToken = linkedCts.Token;
            try
            {
                while (TimeRemaining.Value > TimeSpan.Zero && !recordingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), recordingToken).ConfigureAwait(false);
                    TimeRemaining.Report(TimeRemaining.Value.Subtract(TimeSpan.FromSeconds(1)));
                }
            }
            catch (OperationCanceledException) when (recordingToken.IsCancellationRequested)
            {
                _logger.LogTrace("Transient recording cancelled");
            }
            finally
            {
                IsRecording = false;
            }
        }, nameof(StartRecording));
    }

    public async Task WaitForRecording(CancellationToken token)
    {
        while (IsRecording)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    public void Record(GameObjectHandler owner, string gamePath, string filePath, bool alreadyTransient)
    {
        if (!IsRecording)
        {
            return;
        }

        lock (_recordingLock)
        {
            _recordedTransients.Add(new(owner, gamePath, filePath, alreadyTransient)
            {
                AddTransient = !alreadyTransient,
            });
        }
    }

    public IReadOnlyList<TransientRecord> SaveRecording()
    {
        lock (_recordingLock)
        {
            var selected = _recordedTransients
                .Where(item => item.AddTransient && !item.AlreadyTransient)
                .ToList();
            _recordedTransients.Clear();
            return selected;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _backgroundTasks.StopAccepting();
        _runtimeCts.Cancel();
        _backgroundTasks.StopSynchronously(_logger, TimeSpan.FromSeconds(2), nameof(TransientRecordingService));
        _runtimeCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _backgroundTasks.StopAccepting();
        await _runtimeCts.CancelAsync().ConfigureAwait(false);
        await _backgroundTasks.StopAsync().ConfigureAwait(false);
        _runtimeCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
