using System.Text.Json;
namespace Snowcloak.Core.EnvironmentSnapshots;

public sealed class RestoreEntry
{
    public string Destination { get; set; } = "";
    public string Staged { get; set; } = "";
    public string Original { get; set; } = "";
    public bool Existed { get; set; }
    public bool Applying { get; set; }
    public string? ExpectedOriginalHash { get; set; }
    public string StagedHash { get; set; } = "";
    public bool PluginJson { get; set; }
    public bool Applied { get; set; }
}
public sealed class RestoreJournal
{
    public Guid Snapshot { get; set; }
    public Guid? RepairOperationId { get; set; }
    public string[] RepairHashes { get; set; } = [];
    public string ModRoot { get; set; } = "";
    public Dictionary<string, bool> LoadedPlugins { get; set; } = [];
    public Dictionary<Guid, Guid> IdentityMappings { get; set; } = [];
    public Dictionary<string, Dictionary<Guid, Guid>> PluginIdentityMappings { get; set; } = [];
    public bool RolledBack { get; set; }
    public List<RestoreEntry> Entries { get; set; } = [];
    public bool Consistent { get; set; }
    public static RestoreJournal Load(string path) => JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(path)) ?? throw new InvalidDataException("Unreadable recovery journal.");
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, this); stream.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    public void Install(string journalPath, CancellationToken ct, Action<RestoreEntry>? validate = null, Action<RestoreEntry>? installed = null)
    {
        Consistent = false; Save(journalPath);
        foreach (var entry in Entries)
        {
            ct.ThrowIfCancellationRequested();
            validate?.Invoke(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
            // Write intent durably before touching the destination. Recovery can repeat rollback.
            if (!entry.Applying)
            {
                entry.Existed = File.Exists(entry.Destination);
                if (entry.Existed) { Directory.CreateDirectory(Path.GetDirectoryName(entry.Original)!); if (!File.Exists(entry.Original)) File.Copy(entry.Destination, entry.Original, false); }
                entry.Applying = true; Save(journalPath);
            }
            var pending = entry.Destination + ".snowcloak-restore";
            File.Copy(entry.Staged, pending, true); File.Move(pending, entry.Destination, true);
            entry.Applied = true; Save(journalPath); installed?.Invoke(entry);
        }
    }
    public async Task VerifyInstalledAsync(Func<string, CancellationToken, Task<string>> hash, CancellationToken ct)
    {
        foreach (var entry in Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.Applied || !File.Exists(entry.Destination)
                || await hash(entry.Destination, ct).ConfigureAwait(false) != entry.StagedHash)
                throw new InvalidDataException("Restore copy verification failed: " + entry.Destination);
        }
    }
    public async Task VerifyRollbackAsync(Func<string, CancellationToken, Task<string>> hash, CancellationToken ct)
    {
        foreach (var entry in Entries)
        {
            ct.ThrowIfCancellationRequested();
            // Existing files never reached by mutation need no rollback verification.
            if (!entry.Existed && entry.ExpectedOriginalHash != null) continue;
            if (entry.ExpectedOriginalHash is { } expected)
            {
                if (!File.Exists(entry.Destination) || await hash(entry.Destination, ct).ConfigureAwait(false) != expected)
                    throw new InvalidDataException("Rollback verification failed: " + entry.Destination);
            }
            else if (entry.Existed)
            {
                if (!File.Exists(entry.Destination) || !File.Exists(entry.Original)
                    || await hash(entry.Destination, ct).ConfigureAwait(false) != await hash(entry.Original, ct).ConfigureAwait(false))
                    throw new InvalidDataException("Rollback verification failed: " + entry.Destination);
            }
            else if (File.Exists(entry.Destination))
                throw new InvalidDataException("Rollback left an unexpected file: " + entry.Destination);
        }
    }
    public void Rollback(string journalPath)
    {
        foreach (var entry in Entries.AsEnumerable().Reverse().Where(e => e.Applying))
        {
            if (entry.Existed) File.Copy(entry.Original, entry.Destination, true);
            else if (File.Exists(entry.Destination)) File.Delete(entry.Destination);
            if (File.Exists(entry.Destination + ".snowcloak-restore")) File.Delete(entry.Destination + ".snowcloak-restore");
            entry.Applied = false; entry.Applying = false; Save(journalPath);
        }
        Consistent = true; Save(journalPath);
    }
}
