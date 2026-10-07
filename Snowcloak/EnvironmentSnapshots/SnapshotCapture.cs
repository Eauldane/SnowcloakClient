using System.Text;
using System.Text.Json;
using Blake3;
using Snowcloak.API.Dto.Backups;
using Snowcloak.Core.EnvironmentSnapshots;

namespace Snowcloak.EnvironmentSnapshots;

public sealed record CapturedSnapshot(string Staging, List<BackupFileDto> Files, List<BackupDocumentDto> Documents, string ContentHash) : IDisposable
{
    public Dictionary<string, List<string>> GamePathHints { get; init; } = [];
    public Dictionary<string, List<string>> SourcesByHash { get; init; } = [];
    public Task StageMissingAsync(BackupFileDto file, CancellationToken ct) => Task.Run(async () =>
    {
        var destination = Path.Combine(Staging, file.Hash); if (File.Exists(destination)) return;
        foreach (var sourcePath in SourcesByHash[file.Hash])
        {
            ct.ThrowIfCancellationRequested();
            var resolvedSource = SnapshotSafety.Source(Path.GetDirectoryName(sourcePath)!, Path.GetFileName(sourcePath));
            var pending = destination + ".pending";
            try
            {
                await using (var source = new FileStream(resolvedSource, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var target = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous)) await source.CopyToAsync(target, ct).ConfigureAwait(false);
                if (new FileInfo(pending).Length == file.Size && await SnapshotCapture.HashAsync(pending, ct).ConfigureAwait(false) == file.Hash) { File.Move(pending, destination); return; }
            }
            catch (FileNotFoundException) { }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        throw new IOException("Missing content changed after capture; the previous snapshot is preserved.");
    }, ct);
    public void Dispose() { if (Directory.Exists(Staging)) Directory.Delete(Staging, true); }
}
public sealed class SnapshotCapture
{
    public static Task<CapturedSnapshot> CaptureAsync(string modRoot, string[] mods, IReadOnlyDictionary<string, (string Root, string Version)> pluginRoots, string stagingRoot, CancellationToken ct, Func<CancellationToken, Task>? yield = null, Action<SnapshotCaptureProgress>? progress = null)
        => Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                string stage = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
                try
                {
                    var inputs = Enumerate(modRoot, mods, pluginRoots, ct, progress, attempt + 1);
                    int checkedFiles = 0;
                    progress?.Invoke(new(0, inputs.Count * 2, attempt + 1, ""));
                    var files = new List<BackupFileDto>(); var docs = new List<BackupDocumentDto>();
                    var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var sourcesByHash = new Dictionary<string, List<string>>();
                    foreach (var input in inputs)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (yield != null) await yield(ct).ConfigureAwait(false);
                        progress?.Invoke(new(checkedFiles, inputs.Count * 2, attempt + 1, input.Mod + "/" + input.Path));
                        if (input.Plugin == null)
                        {
                            var rawHash = await HashAsync(input.FullPath, ct).ConfigureAwait(false); observed[input.FullPath] = rawHash;
                            files.Add(new() { Mod = input.Mod, Path = input.Path, Hash = rawHash, Size = new FileInfo(input.FullPath).Length });
                            if (!sourcesByHash.TryGetValue(rawHash, out var sources)) sourcesByHash[rawHash] = sources = [];
                            sources.Add(input.FullPath);
                            progress?.Invoke(new(++checkedFiles, inputs.Count * 2, attempt + 1, input.Mod + "/" + input.Path)); continue;
                        }
                        string pending = Path.Combine(stage, Guid.NewGuid().ToString("N"));
                        await using (var source = new FileStream(input.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        await using (var target = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                            await source.CopyToAsync(target, ct).ConfigureAwait(false);
                        var hash = await HashAsync(pending, ct).ConfigureAwait(false); observed[input.FullPath] = hash;
                        if (input.Plugin != null)
                        {
                            string json;
                            if (input.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    json = await File.ReadAllTextAsync(pending, new UTF8Encoding(false, true), ct).ConfigureAwait(false);
                                    using var parsed = JsonDocument.Parse(json);
                                }
                                catch (DecoderFallbackException)
                                {
                                    json = PersistentDocumentCodec.EncodeBytes(await File.ReadAllBytesAsync(pending, ct).ConfigureAwait(false));
                                }
                                catch (JsonException ex) { throw new InvalidDataException($"Invalid {input.Plugin} settings {input.Path}: {ex.Message}", ex); }
                            }
                            else
                            {
                                var bytes = await File.ReadAllBytesAsync(pending, ct).ConfigureAwait(false);
                                json = PersistentDocumentCodec.EncodeBytes(bytes);
                            }
                            docs.Add(new() { Plugin = input.Plugin, Path = input.Path, Json = json, Version = pluginRoots[input.Plugin].Version });
                            File.Delete(pending);
                        }
                        progress?.Invoke(new(++checkedFiles, inputs.Count * 2, attempt + 1, input.Mod + "/" + input.Path));
                    }
                    var after = Enumerate(modRoot, mods, pluginRoots, ct, progress, attempt + 1);
                    progress?.Invoke(new(checkedFiles, inputs.Count * 2, attempt + 1, ""));
                    bool stable = inputs.SequenceEqual(after);
                    foreach (var input in after)
                    {
                        if (yield != null) await yield(ct).ConfigureAwait(false);
                        if (stable && await HashAsync(input.FullPath, ct).ConfigureAwait(false) != observed[input.FullPath]) stable = false;
                        progress?.Invoke(new(++checkedFiles, inputs.Count * 2, attempt + 1, input.Mod + "/" + input.Path));
                    }
                    if (!stable) { Directory.Delete(stage, true); continue; }
                    files = files.OrderBy(f => f.Mod, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.Ordinal).ToList();
                    docs = docs.OrderBy(d => d.Plugin, StringComparer.Ordinal).ThenBy(d => d.Path, StringComparer.Ordinal).ToList();
                    var digest = Hasher.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Files = files, Documents = docs }))).ToString().ToUpperInvariant();
                    var hints = new Dictionary<string, List<string>>();
                    var filesByPath = files.ToDictionary(f => f.Mod + "/" + f.Path, StringComparer.OrdinalIgnoreCase);
                    foreach (var definition in files.Where(f => Path.GetFileName(f.Path).Equals("default_mod.json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f.Path).StartsWith("group_", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                    {
                        var rawDefinition = await File.ReadAllBytesAsync(sourcesByHash[definition.Hash][0], ct).ConfigureAwait(false);
                        if (Hasher.Hash(rawDefinition).ToString().ToUpperInvariant() != definition.Hash) throw new IOException("Package metadata changed during capture.");
                        int offset = rawDefinition.Length >= 3 && rawDefinition[0] == 0xEF && rawDefinition[1] == 0xBB && rawDefinition[2] == 0xBF ? 3 : 0;
                        JsonDocument json;
                        try { json = JsonDocument.Parse(rawDefinition.AsMemory(offset)); }
                        catch (JsonException ex) { throw new InvalidDataException($"Invalid package metadata {definition.Mod}/{definition.Path}: {ex.Message}", ex); }
                        using (json) AddGamePaths(json.RootElement, definition.Mod, filesByPath, hints);
                    }
                    return new CapturedSnapshot(stage, files, docs, digest) { GamePathHints = hints, SourcesByHash = sourcesByHash };
                }
                catch (IOException) when (attempt < 2) { Directory.Delete(stage, true); }
                catch { Directory.Delete(stage, true); throw; }
            }
            throw new IOException("Selected content changed during three capture attempts. Retry when changes settle.");
        }, ct);
    private static void AddGamePaths(JsonElement node, string mod, IReadOnlyDictionary<string, BackupFileDto> files, Dictionary<string, List<string>> hints)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
            {
                if (property.NameEquals("Files") && property.Value.ValueKind == JsonValueKind.Object)
                    foreach (var mapping in property.Value.EnumerateObject())
                    {
                        if (mapping.Value.ValueKind != JsonValueKind.String) continue;
                        var physical = mapping.Value.GetString()!.Replace('\\', '/');
                        files.TryGetValue(mod + "/" + physical, out var content);
                        if (content == null) throw new InvalidDataException("Unsupported package mapping: physical file is outside the captured package or missing.");
                        if (!hints.TryGetValue(content.Hash, out var paths)) hints[content.Hash] = paths = [];
                        paths.Add(mapping.Name);
                    }
                else AddGamePaths(property.Value, mod, files, hints);
            }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) AddGamePaths(item, mod, files, hints);
    }
    private sealed record Input(string FullPath, string Mod, string Path, string? Plugin);
    private static List<Input> Enumerate(string modRoot, IEnumerable<string> mods, IReadOnlyDictionary<string, (string Root, string Version)> pluginRoots, CancellationToken ct, Action<SnapshotCaptureProgress>? progress, int attempt)
    {
        var result = new List<Input>();
        progress?.Invoke(new(0, 0, attempt, "", SnapshotPhase.Enumerating));
        void Add(Input input)
        {
            ct.ThrowIfCancellationRequested(); result.Add(input);
            progress?.Invoke(new(result.Count, 0, attempt, input.Mod + "/" + input.Path, SnapshotPhase.Enumerating));
        }
        foreach (var mod in mods.Order(StringComparer.Ordinal))
        {
            var directory = SnapshotSafety.Source(modRoot, mod);
            if (!Directory.Exists(directory)) throw new IOException("A selected mod no longer exists.");
            foreach (var file in Walk(directory, ct)) Add(new(SnapshotSafety.Source(directory, Path.GetRelativePath(directory, file).Replace('\\', '/')), mod, Path.GetRelativePath(directory, file).Replace('\\', '/'), null));
        }
        foreach (var (plugin, settings) in pluginRoots.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var settingsRoot = SnapshotSafety.SourceRoot(settings.Root);
            if (File.Exists(settings.Root + ".json")) Add(new(SnapshotSafety.Source(Path.GetDirectoryName(settings.Root)!, Path.GetFileName(settings.Root) + ".json"), "@" + plugin, "@config.json", plugin));
            foreach (var file in Walk(settingsRoot, ct))
            {
                var relative = Path.GetRelativePath(settingsRoot, file).Replace('\\', '/');
                if (!PersistentPluginFiles.Include(relative)) continue;
                Add(new(SnapshotSafety.Source(settingsRoot, relative), "@" + plugin, relative, plugin));
            }
        }
        SnapshotSafety.AssertUnique(result.Select(r => r.Mod + "/" + r.Path));
        return result.OrderBy(i => i.FullPath, StringComparer.Ordinal).ToList();
    }
    private static IEnumerable<string> Walk(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Plugin settings directory was not found.");
        root = SnapshotSafety.SourceRoot(root);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var pending = new Stack<(string Directory, HashSet<string> Ancestors)>();
        pending.Push((root, new(comparer) { root }));
        while (pending.TryPop(out var next))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(next.Directory))
            {
                ct.ThrowIfCancellationRequested();
                SnapshotSafety.Source(root, Path.GetRelativePath(root, file).Replace('\\', '/'));
                yield return file;
            }
            foreach (var child in Directory.EnumerateDirectories(next.Directory))
            {
                ct.ThrowIfCancellationRequested();
                var physical = SnapshotSafety.Source(root, Path.GetRelativePath(root, child).Replace('\\', '/'));
                if (next.Ancestors.Contains(physical)) throw new InvalidDataException("Backup source directory link cycle: " + child);
                var ancestors = new HashSet<string>(next.Ancestors, comparer) { physical };
                pending.Push((child, ancestors));
            }
        }
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        using var hash = Hasher.New(); var buffer = new byte[131072];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        int read; while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) hash.Update(buffer.AsSpan(0, read));
        return hash.Finalize().ToString().ToUpperInvariant();
    }
}
