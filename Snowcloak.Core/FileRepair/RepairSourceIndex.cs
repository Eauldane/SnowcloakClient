using System.Text.Json;
using Snowcloak.Core.EnvironmentSnapshots;
namespace Snowcloak.Core.FileRepair;
public sealed record RepairSource(string Hash, long Size, string Root, string Relative);
public sealed class RepairSourceIndex
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, List<RepairSource>> _accounts = [];
    private bool _loaded;
    public RepairSourceIndex(string path) => _path = path;
    private void EnsureLoaded()
    {
        if (_loaded) return;
        try { _accounts = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, List<RepairSource>>>(File.ReadAllText(_path)) ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException) { _accounts = []; }
        _loaded = true;
    }
    public void Replace(string account, IEnumerable<RepairSource> sources)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _accounts[account] = sources.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_accounts)); File.Move(_path + ".tmp", _path, true);
        }
    }
    public string[] Paths(string account, string hash)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var paths = new List<string>();
            foreach (var source in (_accounts.GetValueOrDefault(account) ?? []).Where(s => s != null && string.Equals(s.Hash, hash, StringComparison.OrdinalIgnoreCase)))
            {
                try { paths.Add(SnapshotSafety.Source(source.Root, source.Relative)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
            }
            return paths.ToArray();
        }
    }
    public static async Task<bool> StageAsync(string source, string staged, string hash, Func<string, CancellationToken, Task<string>> hashFile, CancellationToken ct, long maximumBytes = 2L * 1024 * 1024 * 1024)
    {
        var resolved = SnapshotSafety.Source(Path.GetDirectoryName(source)!, Path.GetFileName(source));
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        bool created = false;
        try
        {
            await using (var input = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                created = true;
                if (input.Length > maximumBytes) throw new InvalidDataException("Repair source exceeds transfer bounds.");
                byte[] buffer = new byte[131072]; long copied = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    copied += read;
                    if (copied > maximumBytes) throw new InvalidDataException("Repair source exceeds transfer bounds.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            if (string.Equals(await hashFile(staged, ct).ConfigureAwait(false), hash, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { if (created) File.Delete(staged); throw; }
        File.Delete(staged); return false;
    }
}
