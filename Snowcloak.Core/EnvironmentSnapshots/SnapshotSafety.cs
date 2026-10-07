using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Snowcloak.Core.EnvironmentSnapshots;

public static class SnapshotSafety
{
    private static string RelativePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/'))
            throw new InvalidDataException("Invalid relative destination.");
        foreach (var segment in relative.Split('/'))
        {
            var stem = segment.Split('.')[0];
            if (segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.Any(c => c < 32 || "<>|?*".Contains(c)) || IsDevice(stem))
                throw new InvalidDataException("Unsafe destination.");
        }
        root = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Destination escapes its root.");
        return target;
    }
    public static string Destination(string root, string relative)
    {
        RelativePath(root, relative);
        var missing = new Stack<string>();
        var existing = Path.GetFullPath(root);
        while (!Exists(existing))
        {
            missing.Push(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing) ?? throw new IOException("Destination root is unavailable.");
        }
        var physicalRoot = SourceRoot(existing);
        foreach (var part in missing) physicalRoot = Path.Combine(physicalRoot, part);
        var target = RelativePath(physicalRoot, relative);
        var current = physicalRoot;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if (!Exists(current)) continue;
            if (!SamePath(SourceRoot(current), current))
                throw new InvalidDataException("Linked destination redirects beneath its selected root: " + current);
        }
        return target;
    }
    public static void VerifyDestination(string destination)
    {
        var full = Path.GetFullPath(destination);
        var resolved = Destination(Path.GetDirectoryName(full)!, Path.GetFileName(full));
        if (!SamePath(full, resolved)) throw new InvalidDataException("Restore destination changed after preview: " + full);
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    public static string SourceRoot(string root) => OperatingSystem.IsWindows()
        ? NativeSourcePath.Resolve(Path.GetFullPath(root))
        : ResolveSource(Path.GetFullPath(root), 0);
    public static string Source(string root, string relative)
    {
        var logical = RelativePath(root, relative);
        var physicalRoot = SourceRoot(root).TrimEnd(Path.DirectorySeparatorChar);
        var physical = SourceRoot(logical);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!physical.StartsWith(physicalRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Backup source link escapes its configured root: " + logical);
        return physical;
    }
    private static string ResolveSource(string path, int depth)
    {
        if (depth > 40) throw new InvalidDataException("Backup source link cycle or excessive link depth: " + path);
        var volume = Path.GetPathRoot(path)!;
        var current = volume;
        foreach (var part in path[volume.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            var target = info.ResolveLinkTarget(true) ?? throw new InvalidDataException("Unsupported backup source link: " + current);
            if (!target.Exists) throw new IOException("Backup source link target is unavailable: " + current);
            current = ResolveSource(target.FullName, depth + 1);
        }
        return current;
    }
    private static bool IsDevice(string name) => new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$" }.Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9';
    public static void AssertUnique(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) if (!seen.Add(path)) throw new InvalidDataException("Case-colliding destinations.");
    }
    public static string CustomizePlusCode(string json, byte version)
    {
        using var result = new MemoryStream();
        using (var gzip = new GZipStream(result, CompressionLevel.Optimal, true))
        {
            gzip.WriteByte(version); gzip.Write(Encoding.UTF8.GetBytes(json));
        }
        return Convert.ToBase64String(result.ToArray());
    }
    public static JsonNode Remap(JsonNode document, IReadOnlyDictionary<Guid, Guid> identities)
    {
        var copy = document.DeepClone(); Rewrite(copy, identities); return copy;
    }
    private static void Rewrite(JsonNode node, IReadOnlyDictionary<Guid, Guid> identities)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                var child = obj[key];
                if (child is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) && identities.TryGetValue(id, out var replacement)) obj[key] = replacement.ToString();
                else if (child != null) Rewrite(child, identities);
                if (Guid.TryParse(key, out var keyId) && identities.TryGetValue(keyId, out var renamed)) { var keyValue = obj[key]; obj.Remove(key); obj.Add(renamed.ToString(), keyValue); }
            }
        }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) && identities.TryGetValue(id, out var replacement)) array[i] = replacement.ToString();
                else if (array[i] != null) Rewrite(array[i]!, identities);
            }
    }
}

public sealed class DailySnapshotState
{
    public bool Enabled { get; set; }
    public Guid? Snapshot { get; set; }
    public DateTimeOffset? Verified { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? RetryAfter { get; set; }
    public string[] Mods { get; set; } = [];
    public string[] Plugins { get; set; } = ["Penumbra", "Glamourer", "CustomizePlus"];
    public bool AllInstalledMods { get; set; }
    public bool Due(DateTimeOffset now, DateTimeOffset started, bool ready) => Enabled && Snapshot.HasValue && ready
        && now >= started.AddMinutes(5) && (!Verified.HasValue || now >= Verified.Value.AddHours(24))
        && (!RetryAfter.HasValue || now >= RetryAfter.Value);
    public void Success(Guid id, DateTimeOffset now) { Snapshot = id; Verified = now; Failures = 0; RetryAfter = null; }
    public void Failure(DateTimeOffset now) { Failures++; RetryAfter = now.AddMinutes(Math.Min(360, 5 * Math.Pow(2, Math.Min(7, Failures - 1)))); }
}
