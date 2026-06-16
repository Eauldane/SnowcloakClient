using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Infrastructure.Data;

namespace Snowcloak.Configuration;

public sealed class StateDocumentStore : IHostedService, IDisposable
{
    private readonly ILogger<StateDocumentStore> _logger;
    private readonly SqliteStateDocumentStore _store;
    private readonly Lock _lock = new();
    private readonly HashSet<IStateDocument> _dirty = [];
    private readonly CancellationTokenSource _cts = new();
    private Task? _saveLoop;

    public string ConfigurationDirectory { get; }

    public StateDocumentStore(ILogger<StateDocumentStore> logger, SqliteStateDocumentStore store, ConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configStore);

        _logger = logger;
        _store = store;
        ConfigurationDirectory = configStore.ConfigurationDirectory;
    }

    internal void Register(IStateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Load(document);
    }

    internal void Commit(IStateDocument document, Action mutate)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_lock)
        {
            mutate();
            _dirty.Add(document);
        }

        document.NotifyChanged();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Flush();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during state document flush");
        }

        _saveLoop = Task.Run(() => SaveLoop(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_saveLoop != null)
            await _saveLoop.ConfigureAwait(false);

        Flush();
    }

    public void Dispose() => _cts.Dispose();

    private void Load(IStateDocument document)
    {
        var payload = _store.Load(document.FileName);
        if (payload != null)
        {
            if (TryLoadPayload(document, payload, out var value))
            {
                document.SetCurrent(value);
                return;
            }

            if (TryLoadSqliteBackup(document, out value))
            {
                document.SetCurrent(value);
                lock (_lock) _dirty.Add(document);
                return;
            }
        }

        if (TryLoadLegacy(document, out var recovered))
        {
            document.SetCurrent(recovered);
            lock (_lock) _dirty.Add(document);
            return;
        }

        document.SetCurrent(document.CreateDefault());
        lock (_lock) _dirty.Add(document);
    }

    private bool TryLoadPayload(IStateDocument document, string payload, out object value)
    {
        value = null!;
        try
        {
            value = document.LoadFromText(payload);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "State document {File} failed to load from SQLite", document.FileName);
            return false;
        }
    }

    private bool TryLoadSqliteBackup(IStateDocument document, out object value)
    {
        value = null!;
        foreach (var payload in _store.LoadBackups(document.FileName))
        {
            try
            {
                value = document.LoadFromText(payload);
                _logger.LogWarning("State document {File} recovered from SQLite backup", document.FileName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping unreadable SQLite backup for state document {File}", document.FileName);
                continue;
            }
        }

        return false;
    }

    private bool TryLoadLegacy(IStateDocument document, out object value)
    {
        value = null!;
        foreach (var fileName in document.LegacyFileNames)
        {
            var path = Path.Combine(ConfigurationDirectory, fileName);
            if (File.Exists(path))
            {
                try
                {
                    value = document.LoadFromText(File.ReadAllText(path));
                    return true;
                }
                catch (Exception ex)
                {
                    QuarantineLegacy(document, path, ex);
                }
            }

            if (TryLoadLegacyBackup(document, fileName, out value))
                return true;
        }

        return false;
    }

    private void QuarantineLegacy(IStateDocument document, string path, Exception ex)
    {
        try
        {
            var quarantinePath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            if (File.Exists(quarantinePath))
                quarantinePath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";

            File.Move(path, quarantinePath);
            _logger.LogError(ex, "Legacy state document {File} failed to load; quarantined to {Dest}", document.FileName, quarantinePath);
        }
        catch (Exception moveEx)
        {
            _logger.LogError(moveEx, "Legacy state document {File} failed to load and could not be quarantined", document.FileName);
        }
    }

    private bool TryLoadLegacyBackup(IStateDocument document, string fileName, out object value)
    {
        value = null!;
        var backupFolder = Path.Combine(ConfigurationDirectory, ConfigStore.BackupFolder);
        if (!Directory.Exists(backupFolder))
            return false;

        var prefix = fileName.Split('.')[0];
        var backups = Directory.EnumerateFiles(backupFolder, prefix + "*")
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc);

        foreach (var backup in backups)
        {
            try
            {
                value = document.LoadFromText(File.ReadAllText(backup));
                _logger.LogWarning("State document {File} recovered from legacy backup {Backup}", document.FileName, backup);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping unreadable legacy backup {Backup} for state document {File}", backup, document.FileName);
                continue;
            }
        }

        return false;
    }

    private async Task SaveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Flush();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during state document flush");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Flush()
    {
        Dictionary<IStateDocument, string> payloads;
        lock (_lock)
        {
            if (_dirty.Count == 0)
                return;

            payloads = _dirty.ToDictionary(d => d, d => d.SerializeCurrent());
            _dirty.Clear();
        }

        foreach (var (document, payload) in payloads)
        {
            try
            {
                _store.Save(document.FileName, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during state document save of {File}", document.FileName);
                lock (_lock) _dirty.Add(document);
            }
        }
    }
}
