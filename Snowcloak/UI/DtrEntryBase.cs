using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Snowcloak.UI;

public abstract class DtrEntryBase : IDisposable, IHostedService
{
    private static readonly Action<ILogger, string, Exception?> LogStarting =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, nameof(LogStarting)), "Starting {EntryName} DTR entry");

    private static readonly Action<ILogger, string, Exception?> LogDisposing =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(2, nameof(LogDisposing)), "Disposing {EntryName} DTR entry");

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly IDtrBar _dtrBar;
    private readonly string _entryName;
    private readonly Lazy<IDtrBarEntry> _entry;
    private readonly ILogger _logger;
    private bool _disposed;
    private Task? _runTask;

    protected DtrEntryBase(ILogger logger, IDtrBar dtrBar, string entryName)
    {
        _logger = logger;
        _dtrBar = dtrBar;
        _entryName = entryName;
        _entry = new(CreateEntry);
    }

    protected IDtrBarEntry Entry => _entry.Value;

    protected bool HasVisibleEntry => _entry.IsValueCreated && _entry.Value.Shown;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!disposing)
        {
            return;
        }

        if (_entry.IsValueCreated)
        {
            LogDisposing(_logger, _entryName, null);
            HideEntry();
            _entry.Value.Remove();
        }

        _cancellationTokenSource.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogStarting(_logger, _entryName, null);
        _runTask = Task.Run(RunAsync, _cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        if (_runTask == null)
        {
            return;
        }

        try
        {
            await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            _runTask = null;
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            _runTask = null;
        }
    }

    protected void HideEntry()
    {
        if (!_entry.IsValueCreated)
        {
            return;
        }

        ResetCachedState();
        _entry.Value.Shown = false;
    }

    protected void ShowEntry()
    {
        if (!Entry.Shown)
        {
            Entry.Shown = true;
        }
    }

    protected abstract void ConfigureEntry(IDtrBarEntry entry);

    protected abstract void ResetCachedState();

    protected abstract void UpdateEntry();

    private IDtrBarEntry CreateEntry()
    {
        var entry = _dtrBar.Get(_entryName);
        ConfigureEntry(entry);
        return entry;
    }

    private async Task RunAsync()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            await Task.Delay(1000, _cancellationTokenSource.Token).ConfigureAwait(false);
            UpdateEntry();
        }
    }
}
