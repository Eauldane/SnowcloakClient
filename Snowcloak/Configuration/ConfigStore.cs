using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Snowcloak.Configuration;

public sealed class ConfigStore : IHostedService, IDisposable
{
    public const string BackupFolder = "config_backup";
    private const int MaxBackups = 10;

    private readonly ILogger<ConfigStore> _logger;
    private readonly Lock _lock = new();
    private readonly HashSet<IConfigDocument> _dirty = [];
    private readonly CancellationTokenSource _cts = new();
    private Task? _saveLoop;

    public string ConfigurationDirectory { get; }

    public ConfigStore(ILogger<ConfigStore> logger, string configDirectory)
    {
        _logger = logger;
        ConfigurationDirectory = configDirectory;
    }

    internal void Register(IConfigDocument document) => Load(document);

    internal void Commit(IConfigDocument document, Action mutate)
    {
        lock (_lock)
        {
            mutate();
            _dirty.Add(document);
        }

        document.RaiseChanged();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
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

    private void Load(IConfigDocument document)
    {
        var path = Path.Combine(ConfigurationDirectory, document.FileName);

        if (File.Exists(path))
        {
            try
            {
                document.SetCurrent(document.LoadFromText(File.ReadAllText(path)));
                return;
            }
            catch (Exception ex)
            {
                Quarantine(document, path, ex);
            }
        }

        if (TryLoadBackup(document, out var recovered))
        {
            document.SetCurrent(recovered);
            lock (_lock) _dirty.Add(document);
            return;
        }

        document.SetCurrent(document.CreateDefault());
        lock (_lock) _dirty.Add(document);
    }

    private void Quarantine(IConfigDocument document, string path, Exception ex)
    {
        try
        {
            var quarantinePath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            if (File.Exists(quarantinePath))
                quarantinePath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";

            File.Move(path, quarantinePath);
            _logger.LogError(ex, "Config {file} failed to load; quarantined to {dest}", document.FileName, quarantinePath);
        }
        catch (Exception moveEx)
        {
            _logger.LogError(moveEx, "Config {file} failed to load and could not be quarantined", document.FileName);
        }
    }

    private bool TryLoadBackup(IConfigDocument document, out object value)
    {
        value = null!;
        var backupFolder = Path.Combine(ConfigurationDirectory, BackupFolder);
        if (!Directory.Exists(backupFolder))
            return false;

        var prefix = document.FileName.Split('.')[0];
        var backups = Directory.EnumerateFiles(backupFolder, prefix + "*")
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc);

        foreach (var backup in backups)
        {
            try
            {
                value = document.LoadFromText(File.ReadAllText(backup));
                _logger.LogWarning("Config {file} recovered from backup {backup}", document.FileName, backup);
                return true;
            }
            catch
            {
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
                _logger.LogError(ex, "Error during config flush");
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
        Dictionary<IConfigDocument, string> payloads;
        lock (_lock)
        {
            if (_dirty.Count == 0)
                return;

            payloads = _dirty.ToDictionary(d => d, d => d.SerializeCurrent());
            _dirty.Clear();
        }

        foreach (var (document, json) in payloads)
        {
            try
            {
                WriteAtomic(document, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during config save of {file}", document.FileName);
                lock (_lock) _dirty.Add(document);
            }
        }
    }

    private void WriteAtomic(IConfigDocument document, string json)
    {
        var path = Path.Combine(ConfigurationDirectory, document.FileName);
        Backup(document, path);

        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private void Backup(IConfigDocument document, string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            try
            {
                JsonNode.Parse(File.ReadAllText(path));
            }
            catch
            {
                return;
            }

            var backupFolder = Path.Combine(ConfigurationDirectory, BackupFolder);
            Directory.CreateDirectory(backupFolder);

            var split = document.FileName.Split('.');
            var prefix = split[0];
            var ext = split.Length > 1 ? split[^1] : "json";

            var existing = Directory.EnumerateFiles(backupFolder, prefix + "*")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var old in existing.Skip(MaxBackups))
                old.Delete();

            var backupPath = Path.Combine(backupFolder, $"{prefix}.{DateTime.Now:yyyyMMddHHmmss}.{ext}");
            File.Copy(path, backupPath, overwrite: true);
            new FileInfo(backupPath).LastWriteTimeUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create backup for {file}", document.FileName);
        }
    }
}
