using System.Text;
using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Snowcloak.API.Dto.Backups;
using Snowcloak.CacheFile;
using Snowcloak.Core.EnvironmentSnapshots;
using Snowcloak.FileCache;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI.Files;

namespace Snowcloak.EnvironmentSnapshots;

public sealed class RestoreChoice
{
    public string Title { get; set; } = "";
    public bool Unchanged { get; set; }
    public string Destination { get; set; } = "";
    public string? ExistingHash { get; set; }
    public bool? Available { get; set; }
    public DateTime? SharedProtectionUntil { get; set; }
    public BackupFileDto? File { get; set; }
    public BackupDocumentDto? Document { get; set; }
    public bool Conflict { get; set; }
    public bool GlobalSettings { get; set; }
}
public sealed class SnapshotRestoreService : IAsyncDisposable
{
    private readonly SnapshotService _backups;
    private readonly Snowcloak.FileRepair.FileRepairService _repair;
    public bool CanRepair => _repair.Available;
    public bool RepairUnavailable { get; private set; }
    private readonly PluginLifecycleAdapter _lifecycle;
    private readonly IDalamudPluginInterface _pi;
    private readonly FileTransferOrchestrator _transfers;
    private readonly FileCacheManager _cache;
    private readonly CacheMonitor _monitor;
    private readonly SnowMediator _mediator;
    private IDisposable? _lease;
    private RestoreJournal? _journal;
    private bool _unreadableRecovery;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource _operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SnapshotProgress Progress { get; } = new();
    public string? Error { get; private set; }
    public bool Previewing { get; private set; }
    public string Status { get; private set; } = "Select a complete snapshot to restore.";
    public List<RestoreChoice> Choices { get; private set; } = [];
    public bool Busy => _operation != null;
    public bool AwaitingUnload { get; private set; }
    public bool AwaitingReload { get; private set; }
    public bool HasRecoveryData => _journal != null || _unreadableRecovery || Directory.Exists(RecoveryArchiveRoot);
    public bool RequiredPluginsUnloaded => !_pi.InstalledPlugins.Any(p => RequiredPlugins.Contains(p.InternalName) && p.IsLoaded);
    public bool PluginShouldBeLoaded(string plugin) => _journal?.LoadedPlugins.GetValueOrDefault(plugin) == true;
    public bool PluginLoaded(string plugin) => _pi.InstalledPlugins.Any(p => p.InternalName == plugin && p.IsLoaded);
    public bool NeedsRecovery => _unreadableRecovery || _journal?.Consistent == false;
    public string[] RequiredPlugins { get; private set; } = [];
    public Guid Snapshot { get; private set; }
    public string ModRoot { get; set; } = "";
    public bool AllowPartial { get; set; }
    public bool RolledBack { get; private set; }
    public bool ManualVerified { get; set; }
    public Dictionary<string, string> CustomizePlusCodes { get; private set; } = [];
    private string RecoveryArchiveRoot => Path.Combine(_pi.ConfigDirectory.FullName, "restore-recovery-history");
    private string JournalPath => Path.Combine(_pi.ConfigDirectory.FullName, "restore-recovery", "journal.json");
    public SnapshotRestoreService(SnapshotService backups, IDalamudPluginInterface pi, FileTransferOrchestrator transfers, FileCacheManager cache, CacheMonitor monitor, SnowMediator mediator, Snowcloak.FileRepair.FileRepairService repair)
    {
        _repair = repair; _backups = backups; _pi = pi; _lifecycle = new(pi); _transfers = transfers; _cache = cache; _monitor = monitor; _mediator = mediator;
        if (File.Exists(JournalPath))
        {
            try { _journal = RestoreJournal.Load(JournalPath); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                _lease = SnapshotExclusion.AcquireAsync(CancellationToken.None).GetAwaiter().GetResult();
                _unreadableRecovery = true; Error = "Recovery journal unreadable · Sync suspended"; Progress.Phase(SnapshotPhase.Failed); Status = "Recovery journal is unreadable. Sync stays suspended; preserve recovery data and repair the journal before continuing."; return;
            }
            if (!_journal.Consistent)
            {
                _lease = SnapshotExclusion.AcquireAsync(CancellationToken.None).GetAwaiter().GetResult();
                ModRoot = _journal.ModRoot; Snapshot = _journal.Snapshot;
                RequiredPlugins = _journal.LoadedPlugins.Keys.ToArray(); RolledBack = _journal.RolledBack;
                Status = "Interrupted restore: syncing is suspended. Resume the restore or roll back before resuming.";
            }
        }
    }
    public Task PreviewAsync(Guid id) => PreviewCoreAsync(id, false);
    private async Task PreviewCoreAsync(Guid id, bool recovering)
    {
        if (Busy || Previewing || _lease != null && !recovering) return;
        RepairUnavailable = false; Previewing = true; Error = null; Progress.Reset(); Progress.Phase(SnapshotPhase.Preview);
        try
        {
            var summary = await _backups.ReadAsync<BackupSummaryDto>($"backups/{id}/summary").ConfigureAwait(false);
            if (!summary.Complete) throw new InvalidOperationException("Only complete snapshots can be restored.");
            Snapshot = id; if (string.IsNullOrWhiteSpace(ModRoot)) ModRoot = _backups.ModRoot ?? ""; var previewChoices = new List<RestoreChoice>(); var previewCodes = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(ModRoot)) throw new IOException("Select a usable Penumbra destination before preview.");
            for (int offset = 0; ; offset += 256)
            {
                var page = await _backups.ReadAsync<BackupPageDto>($"backups/{id}/page?offset={offset}").ConfigureAwait(false);
                foreach (var file in page.Files)
                {
                    if (file.Mod.StartsWith('@') && !PersistentPluginFiles.Include(file.Path)) continue;
                    var target = Target(file.Mod, file.Path);
                    bool exists = System.IO.File.Exists(target);
                    var existingHash = exists ? await SnapshotCapture.HashAsync(target, CancellationToken.None).ConfigureAwait(false) : null;
                    bool equal = existingHash == file.Hash;
                    previewChoices.Add(new() { File = file, ExistingHash = existingHash, Destination = target, Conflict = exists && !equal, Unchanged = equal, GlobalSettings = file.Mod.StartsWith('@') });
                }
                if (page.Files.Count < 256) break;
            }
            foreach (var batch in previewChoices.Where(c => c.File != null).Select(c => c.File!.Hash).Distinct().Chunk(256))
                foreach (var obj in await _backups.CheckObjectsAsync(id, batch, CancellationToken.None).ConfigureAwait(false))
                    foreach (var choice in previewChoices.Where(c => c.File?.Hash == obj.Hash)) { choice.Available = obj.Available && !obj.Forbidden; choice.SharedProtectionUntil = obj.ProtectedUntilUtc; }
            var docs = await _backups.ReadAsync<List<BackupDocumentDto>>($"backups/{id}/documents").ConfigureAwait(false);
            foreach (var document in docs)
            {
                bool packageDocument = ModPackageDocuments.IsPackage(document);
                if (!packageDocument && !PersistentPluginFiles.Include(document.Path)) continue;
                if (packageDocument) _ = ModPackageDocuments.ReadBytes(document);
                var target = TargetDocument(document);
                bool exists = System.IO.File.Exists(target);
                bool binary = PersistentDocumentCodec.IsOpaque(document.Json);
                bool equal = exists && (binary ? Convert.ToBase64String(await System.IO.File.ReadAllBytesAsync(target).ConfigureAwait(false)) == JsonNode.Parse(document.Json)!["SnowcloakOpaqueDocumentV1"]?.GetValue<string>() : JsonNode.DeepEquals(JsonNode.Parse(await System.IO.File.ReadAllTextAsync(target).ConfigureAwait(false)), JsonNode.Parse(document.Json)));
                bool global = !packageDocument && (document.Path == "@config.json" || Path.GetFileName(document.Path).Equals("config.json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(document.Path).Equals("configuration.json", StringComparison.OrdinalIgnoreCase));
                previewChoices.Add(new() { Title = DocumentTitle(document), Document = document, ExistingHash = exists ? await SnapshotCapture.HashAsync(target, CancellationToken.None).ConfigureAwait(false) : null, Destination = target, Conflict = exists && !equal, GlobalSettings = global, Unchanged = equal });
                if (document.Plugin == "CustomizePlus" && document.Path.StartsWith("templates/", StringComparison.OrdinalIgnoreCase) && !binary)
                {
                    var json = JsonNode.Parse(document.Json)!;
                    byte version = json["Version"]?.GetValue<byte>() ?? throw new InvalidDataException("Customize+ template version missing.");
                    previewCodes[document.Path] = SnapshotSafety.CustomizePlusCode(document.Json, version);
                }
            }
            SnapshotSafety.AssertUnique(previewChoices.Select(c => c.Destination));
            Choices = previewChoices; CustomizePlusCodes = previewCodes;
            Status = "Restore preview ready.";
        }
        catch (Exception ex) { Error = ex.Message; Progress.Phase(SnapshotPhase.Failed); Status = "Preview failed: " + ex.Message; Choices = []; }
        finally { Previewing = false; }
    }
    private static string DocumentTitle(BackupDocumentDto document)
    {
        var parsed = JsonNode.Parse(document.Json);
        if (parsed is JsonObject obj && obj["Name"] is JsonValue value && value.TryGetValue<string>(out var name)) return name;
        if (parsed is JsonObject nested && nested["Name"] is JsonObject title && title["Text"] is JsonValue text && text.TryGetValue<string>(out var label)) return label;
        return document.Path == "@config.json" ? "Settings" : Path.GetFileNameWithoutExtension(document.Path).Replace('_', ' ');
    }
    private string TargetDocument(BackupDocumentDto document)
    {
        if (!ModPackageDocuments.IsPackage(document)) return Target("@" + document.Plugin, document.Path);
        if (document.Version != ModPackageDocuments.FormatVersion || !ModPackageDocuments.IsPath(document.Path))
            throw new InvalidDataException("Unsupported mod metadata destination.");
        return SnapshotSafety.Destination(ModRoot, document.Path);
    }
    private string Target(string mod, string relative)
    {
        if (!mod.StartsWith('@')) return SnapshotSafety.Destination(ModRoot, mod + "/" + relative);
        var plugin = mod[1..];
        if (plugin is not ("Penumbra" or "Glamourer" or "CustomizePlus")) throw new InvalidDataException("Unknown plugin destination.");
        if (relative == "@config.json") return SnapshotSafety.Destination(_pi.ConfigDirectory.Parent!.FullName, plugin + ".json");
        return SnapshotSafety.Destination(Path.Combine(_pi.ConfigDirectory.Parent!.FullName, plugin), relative);
    }
    public void ResumeInterruptedRestore()
    {
        if (_journal?.Consistent != false || _lease == null || Busy) return;
        if (!_journal.Entries.Any(e => e.Applying || e.Applied)) { _ = ResumeStagingAsync(_journal); return; }
        if (!_journal.RolledBack && _journal.Entries.All(e => e.Applied))
        {
            RequiredPlugins = _journal.LoadedPlugins.Keys.ToArray(); AwaitingReload = true; Error = null;
            Progress.Phase(SnapshotPhase.Verifying); Status = "Files restored · Verification required";
            return;
        }
        RequiredPlugins = _journal.LoadedPlugins.Keys.ToArray(); AwaitingUnload = true; Error = null; Progress.Phase(SnapshotPhase.Unload);
        Status = "Recovery ready · Resume restore";
    }
    private async Task ResumeStagingAsync(RestoreJournal interrupted)
    {
        try
        {
            await PreviewCoreAsync(interrupted.Snapshot, true).ConfigureAwait(false);
            if (Error != null || Choices.Count == 0) return;
            await StageCoreAsync(interrupted).ConfigureAwait(false);
        }
        catch (Exception ex) { Error = ex.Message; Status = "Recovery needs attention"; }
    }
    public void Cancel() { try { _operation?.Cancel(); } catch (ObjectDisposedException) { } }
    public Task StageAsync() => StageCoreAsync(null);
    private async Task StageCoreAsync(RestoreJournal? interrupted)
    {
        if (Busy || Previewing || _lease != null && interrupted == null || Snapshot == Guid.Empty) return;
        _operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); _operation = new(); var ct = _operation.Token;
        RepairUnavailable = false; Error = null; RolledBack = false; Progress.Reset(); Progress.Phase(SnapshotPhase.Staging);
        try
        {
            if (_journal?.Consistent == true && System.IO.File.Exists(JournalPath)) throw new IOException("Discard previous recovery data before another restore.");
            _lease ??= await SnapshotExclusion.AcquireAsync(ct).ConfigureAwait(false);
            _mediator.Publish(new HaltScanMessage("Environment restore"));
            while (_monitor.IsScanRunning) await Task.Delay(100, ct).ConfigureAwait(false);
            _lifecycle.Validate(_pi.InstalledPlugins.Where(p => p.InternalName is "Penumbra" or "Glamourer" or "CustomizePlus").Select(p => p.InternalName));
            var originalModRoot = _backups.ModRoot;
            if (interrupted != null)
            {
                var oldStage = Path.Combine(_pi.ConfigDirectory.FullName, "restore-recovery");
                if (Directory.Exists(oldStage)) Directory.Delete(oldStage, true);
            }
            _journal = new() { Snapshot = Snapshot, ModRoot = ModRoot, RepairOperationId = interrupted?.RepairOperationId };
            foreach (var plugin in _pi.InstalledPlugins.Where(p => p.InternalName is "Penumbra" or "Glamourer" or "CustomizePlus"))
                _journal.LoadedPlugins.Add(plugin.InternalName, interrupted?.LoadedPlugins.GetValueOrDefault(plugin.InternalName) ?? plugin.IsLoaded);
            RequiredPlugins = _journal.LoadedPlugins.Keys.ToArray();
            _journal.Save(JournalPath);

            foreach (var choice in Choices)
            {
                var target = choice.File is { } saved ? Target(saved.Mod, saved.Path) : TargetDocument(choice.Document!);
                if (target != choice.Destination) throw new IOException("Restore destination changed after preview.");
                choice.ExistingHash = System.IO.File.Exists(target) ? await SnapshotCapture.HashAsync(target, ct).ConfigureAwait(false) : null;
                choice.Unchanged = choice.File is { } file ? choice.ExistingHash == file.Hash : await MatchesDocumentAsync(target, choice.Document!, ct).ConfigureAwait(false);
                choice.Conflict = choice.ExistingHash != null && !choice.Unchanged;
            }
            var selected = Choices.Where(c => !c.Unchanged || c.Document is { } opaqueDocument && PersistentDocumentCodec.IsOpaque(opaqueDocument.Json) || c.Document is { } settings && PenumbraRestoreSettings.IsSettings(settings.Plugin, settings.Path) && PenumbraRestoreSettings.NeedsRoot(settings.Json, ModRoot)).Select(c => new RestoreChoice { ExistingHash = c.ExistingHash, Destination = c.Destination, File = c.File, Document = c.Document, Conflict = c.Conflict, GlobalSettings = c.GlobalSettings }).ToList();
            if (selected.Count == 0)
            {
                await _lifecycle.ChangeAsync(_journal.LoadedPlugins, true, CancellationToken.None).ConfigureAwait(false);
                if (_journal.RepairOperationId is { } completedRepair) await _repair.ReleaseAsync(completedRepair).ConfigureAwait(false);
                _journal.Consistent = true; _journal.Save(JournalPath);
                Status = "Snapshot contents already present."; Progress.Phase(SnapshotPhase.Complete); Release(); return;
            }
            var stage = Path.Combine(_pi.ConfigDirectory.FullName, "restore-recovery"); Directory.CreateDirectory(stage);
            var requiredBytes = selected.Sum(c => c.File?.Size ?? Encoding.UTF8.GetByteCount(c.Document!.Json));
            var rollbackBytes = selected.Where(c => System.IO.File.Exists(c.Destination)).Sum(c => new FileInfo(c.Destination).Length);
            foreach (var driveRoot in selected.Select(c => Path.GetPathRoot(c.Destination)!).Append(Path.GetPathRoot(stage)!).Distinct(StringComparer.OrdinalIgnoreCase))
                if (new DriveInfo(driveRoot).AvailableFreeSpace < checked(requiredBytes + rollbackBytes + 64 * 1024 * 1024)) throw new IOException("Insufficient free space for staging, restore and rollback.");
            var hashes = selected.Where(c => c.File != null).Select(c => c.File!.Hash).Distinct().ToArray();
            if (_repair.Available && hashes.Length > 0)
            {
                _journal.RepairOperationId ??= Guid.NewGuid(); _journal.RepairHashes = hashes; _journal.Save(JournalPath);
                await _repair.ProtectAsync(new() { OperationId = _journal.RepairOperationId.Value, Context = Snowcloak.API.Dto.FileRepair.FileRepairContext.Backup, ContextId = Snapshot.ToString(), Hashes = hashes }, ct).ConfigureAwait(false);
            }
            var local = new Dictionary<string, string>(StringComparer.Ordinal);
            var localUsed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hash in hashes)
            {
                var path = Path.Combine(stage, "stage", Guid.NewGuid().ToString("N"));
                if (await _repair.TryStageLocalAsync(hash, path, ct).ConfigureAwait(false)) local[hash] = path;
            }
            var available = new Dictionary<string, BackupObjectDto>();
            foreach (var batch in hashes.Chunk(256))
                foreach (var obj in await _backups.CheckObjectsAsync(Snapshot, batch, ct).ConfigureAwait(false)) available[obj.Hash] = obj;
            var publicHashes = selected.Where(c => c.File != null && !c.File.Mod.StartsWith('@')).Select(c => c.File!.Hash).ToHashSet(StringComparer.Ordinal);
            var missing = available.Values.Where(o => publicHashes.Contains(o.Hash) && !o.Available && !o.Forbidden && !local.ContainsKey(o.Hash)).Select(o => o.Hash).ToArray();
            if (missing.Length > 0 && _repair.Available)
            {
                Progress.Phase(SnapshotPhase.Downloading, path: "Recovering missing files…");
                await _repair.WaitAsync(new() { OperationId = _journal.RepairOperationId!.Value, Context = Snowcloak.API.Dto.FileRepair.FileRepairContext.Backup, ContextId = Snapshot.ToString(), Hashes = missing }, message => { Status = message; Progress.Phase(SnapshotPhase.Downloading, path: message); }, ct).ConfigureAwait(false);
                foreach (var batch in missing.Chunk(256)) foreach (var obj in await _backups.CheckObjectsAsync(Snapshot, batch, ct).ConfigureAwait(false)) available[obj.Hash] = obj;
            }
            foreach (var choice in Choices.Where(c => c.File != null)) choice.Available = available.GetValueOrDefault(choice.File!.Hash)?.Available ?? choice.Available;
            if (!AllowPartial && available.Values.Any(o => o.Forbidden || !o.Available && !local.ContainsKey(o.Hash))) throw new MissingRepairContentException();
            Progress.Phase(SnapshotPhase.Downloading, selected.Count(c => c.Document != null || (available[c.File!.Hash].Available || local.ContainsKey(c.File.Hash)) && !available[c.File.Hash].Forbidden)); int stagedCount = 0;
            foreach (var choice in selected)
            {
                ct.ThrowIfCancellationRequested();
                string target = choice.File is { } pathFile ? Target(pathFile.Mod, pathFile.Path) : TargetDocument(choice.Document!);
                if ((choice.File is { } package && !package.Mod.StartsWith('@') || choice.Document is { } metadata && ModPackageDocuments.IsPackage(metadata)) && (System.IO.File.Exists(target) ? await SnapshotCapture.HashAsync(target, ct).ConfigureAwait(false) != choice.ExistingHash : choice.ExistingHash != null)) throw new IOException("A destination changed after preview. Review it again.");
                if (!string.Equals(target, choice.Destination, StringComparison.Ordinal)) throw new InvalidDataException("Restore root changed after preview. Review again before staging.");
                var staged = Path.Combine(stage, "stage", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                if (choice.File is { } file)
                {
                    if (available[file.Hash].Forbidden) { if (!AllowPartial) throw new MissingRepairContentException(); continue; }
                    if (local.TryGetValue(file.Hash, out var localPath))
                    {
                        if (localUsed.Add(file.Hash)) { System.IO.File.Move(localPath, staged, false); local[file.Hash] = staged; }
                        else System.IO.File.Copy(localPath, staged, false);
                    }
                    else
                    {
                    if (!available[file.Hash].Available) continue;
                    Progress.StartUpload(file.Mod + "/" + file.Path, 0, SnapshotPhase.Downloading);
                    using var response = await _backups.DownloadObjectAsync(Snapshot, file.Hash, available[file.Hash].Url, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    long received = 0; var length = response.Content.Headers.ContentLength ?? 0;
                    Progress.StartUpload(file.Mod + "/" + file.Path, length, SnapshotPhase.Downloading);
                    await using var input = new SnapshotUploadStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), _ => Task.CompletedTask, ct, bytes => Progress.Transfer(received += bytes, length));
                    await ScfFile.ValidateAndExtractToPathAsync(input, staged, new ScfValidationRequest(new ScfExpectedRawHash(file.Hash), ScfReadLimits.Default), ct).ConfigureAwait(false);
                    Progress.EndTransfer();
                    }
                    if (new FileInfo(staged).Length != file.Size) throw new InvalidDataException("Restored length differs from the manifest.");
                }
                else
                {
                    var document = choice.Document!;
                    var parsed = JsonNode.Parse(document.Json)!;
                    if (!ModPackageDocuments.IsPackage(document))
                    {
                        var installed = _pi.InstalledPlugins.SingleOrDefault(p => p.InternalName == document.Plugin);
                        if (installed == null || installed.Version.ToString() != document.Version) throw new InvalidDataException("Plugin version differs from the snapshot: " + document.Plugin);
                    }
                    else _ = ModPackageDocuments.ReadBytes(document);
                    if (PenumbraRestoreSettings.IsSettings(document.Plugin, document.Path)) parsed = PenumbraRestoreSettings.WithRoot(document.Json, ModRoot);
                    if (PersistentDocumentCodec.IsOpaque(document.Json))
                        await System.IO.File.WriteAllBytesAsync(staged, PersistentDocumentCodec.DecodeBytes(document.Json), ct).ConfigureAwait(false);
                    else await System.IO.File.WriteAllTextAsync(staged, parsed.ToJsonString(), ct).ConfigureAwait(false);
                }
                _journal.Entries.Add(new() { Destination = target, Staged = staged, PluginJson = choice.Document is { } savedDocument && !PersistentDocumentCodec.IsOpaque(savedDocument.Json), ExpectedOriginalHash = target == choice.Destination ? choice.ExistingHash : null, StagedHash = await SnapshotCapture.HashAsync(staged, ct).ConfigureAwait(false), Original = Path.Combine(stage, "originals", Guid.NewGuid().ToString("N")) });
                Progress.Advance(++stagedCount, choice.Document != null ? choice.Document.Plugin + "/" + choice.Document.Path : choice.File!.Mod + "/" + choice.File.Path);
            }
            foreach (var entry in _journal.Entries)
            {
                // Recheck all existing ancestors after staging, before any mutation.
                SnapshotSafety.VerifyDestination(entry.Destination);
                if (entry.Destination != Choices.FirstOrDefault(c => c.Destination == entry.Destination)?.Destination && System.IO.File.Exists(entry.Destination)) throw new IOException("A destination appeared during staging.");
            }
            if (selected.Any(c => c.File != null && !c.File.Mod.StartsWith('@') || c.Document is { } metadata && ModPackageDocuments.IsPackage(metadata)) && !string.Equals(originalModRoot, ModRoot, StringComparison.OrdinalIgnoreCase)
                && !selected.Any(c => c.Document is { } settings && PenumbraRestoreSettings.IsSettings(settings.Plugin, settings.Path)))
            {
                var configPath = PenumbraRestoreSettings.ExistingPath(_pi.ConfigDirectory.Parent!.FullName);
                if (!_journal.Entries.Any(e => e.Destination == configPath))
                {
                    var config = PenumbraRestoreSettings.WithRoot(await System.IO.File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false), ModRoot);
                    var stagedRoot = Path.Combine(stage, "stage", Guid.NewGuid().ToString("N"));
                    await System.IO.File.WriteAllTextAsync(stagedRoot, config.ToJsonString(), ct).ConfigureAwait(false);
                    _journal.Entries.Add(new() { Destination = configPath, Staged = stagedRoot, PluginJson = true, ExpectedOriginalHash = await SnapshotCapture.HashAsync(configPath, ct).ConfigureAwait(false), StagedHash = await SnapshotCapture.HashAsync(stagedRoot, ct).ConfigureAwait(false), Original = Path.Combine(stage, "originals", Guid.NewGuid().ToString("N")) });
                }
            }
            if (_journal.Entries.Count == 0) throw new InvalidOperationException("No restore contents are available.");
            SnapshotSafety.AssertUnique(_journal.Entries.Select(e => e.Destination));
            _journal.Save(JournalPath);
            Progress.Phase(SnapshotPhase.Unload); Status = "Disabling plugins";
            await _lifecycle.ChangeAsync(_journal.LoadedPlugins, false, ct).ConfigureAwait(false);
            foreach (var unchanged in Choices.Where(c => c.Unchanged && c.Document != null && !ModPackageDocuments.IsPackage(c.Document) && !selected.Any(s => s.Destination == c.Destination)))
            {
                var relative = unchanged.Document!.Path == "@config.json" ? unchanged.Document.Plugin + ".json" : unchanged.Document.Plugin + "/" + unchanged.Document.Path;
                if (PluginRestoreVerification.CompareAfterReload(relative)
                    && (!System.IO.File.Exists(unchanged.Destination) || !PluginRestoreVerification.EquivalentJson(relative, unchanged.Document.Json, await System.IO.File.ReadAllTextAsync(unchanged.Destination, ct).ConfigureAwait(false))))
                    throw new IOException("Plugin settings changed during staging. Retry the restore.");
            }
            foreach (var entry in _journal.Entries)
            {
                var choice = Choices.FirstOrDefault(c => c.Destination == entry.Destination);
                if (choice?.Document is { } pluginDocument && !ModPackageDocuments.IsPackage(pluginDocument) || choice == null || choice.File?.Mod.StartsWith('@') == true)
                    entry.ExpectedOriginalHash = System.IO.File.Exists(entry.Destination) ? await SnapshotCapture.HashAsync(entry.Destination, ct).ConfigureAwait(false) : null;
            }
            _journal.Save(JournalPath);
            await CopyStagedAsync(ct).ConfigureAwait(false);
            Progress.Phase(SnapshotPhase.Reload); Status = "Re-enabling plugins";
            await _lifecycle.ChangeAsync(_journal.LoadedPlugins, true, ct).ConfigureAwait(false);
            Status = "Files restored · Verify names, collections and references";
        }
        catch (Exception ex)
        {
            var snapshot = Snapshot; var choices = Choices; var root = ModRoot;
            await RecoverFailedRestoreAsync(ex).ConfigureAwait(false);
            if (ex is MissingRepairContentException && !NeedsRecovery) { Snapshot = snapshot; Choices = choices; ModRoot = root; RepairUnavailable = true; }
        }
        finally { _operation.Dispose(); _operation = null; _operationFinished.TrySetResult(); }
    }
    private async Task CopyStagedAsync(CancellationToken ct)
    {
        if (_journal == null) throw new InvalidOperationException("Recovery journal unavailable.");
        foreach (var entry in _journal.Entries) if (!System.IO.File.Exists(entry.Staged)) throw new IOException("A staged file disappeared.");
        RolledBack = false; _journal.RolledBack = false; ManualVerified = false; Error = null;
        int installedCount = 0; Progress.Phase(SnapshotPhase.Installing, _journal.Entries.Count);
        await Task.Run(() => _journal.Install(JournalPath, ct, entry =>
        {
            if (_pi.InstalledPlugins.Any(p => RequiredPlugins.Contains(p.InternalName) && p.IsLoaded)) throw new InvalidOperationException("An affected plugin was reloaded while copying files.");
            SnapshotSafety.VerifyDestination(entry.Destination);
            if (entry.Existed && System.IO.File.Exists(entry.Original) && SnapshotCapture.HashAsync(entry.Original, ct).GetAwaiter().GetResult() != entry.ExpectedOriginalHash) throw new InvalidDataException("Recovery original changed.");
            if (SnapshotCapture.HashAsync(entry.Staged, ct).GetAwaiter().GetResult() != entry.StagedHash) throw new InvalidDataException("Staged restore bytes changed.");
            if (!entry.Applying && (System.IO.File.Exists(entry.Destination) ? SnapshotCapture.HashAsync(entry.Destination, ct).GetAwaiter().GetResult() != entry.ExpectedOriginalHash : entry.ExpectedOriginalHash != null)) throw new IOException("Destination changed before restore.");
        }, entry => Progress.Advance(++installedCount, Path.GetFileName(entry.Destination)))).ConfigureAwait(false);
        Progress.Phase(SnapshotPhase.Verifying);
        await _journal.VerifyInstalledAsync(SnapshotCapture.HashAsync, ct).ConfigureAwait(false);
        AwaitingUnload = false; AwaitingReload = true; Progress.Phase(SnapshotPhase.Reload);
        Status = "Files restored";
    }
    private sealed class MissingRepairContentException() : IOException("Some files could not be recovered. Retry, restore available files only, or cancel.");
    public async Task InstallAsync()
    {
        if (!AwaitingUnload || _journal == null || Busy || _journal.Entries.Count == 0) return;
        _operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); _operation = new();
        try
        {
            if (_repair.Available && _journal.RepairOperationId is { } repair && _journal.RepairHashes.Length > 0)
                await _repair.ProtectAsync(new() { OperationId = repair, Context = Snowcloak.API.Dto.FileRepair.FileRepairContext.Backup, ContextId = Snapshot.ToString(), Hashes = _journal.RepairHashes }, _operation.Token).ConfigureAwait(false);
            Progress.Phase(SnapshotPhase.Unload);
            await _lifecycle.ChangeAsync(_journal.LoadedPlugins, false, _operation.Token).ConfigureAwait(false);
            await CopyStagedAsync(_operation.Token).ConfigureAwait(false);
            Progress.Phase(SnapshotPhase.Reload);
            await _lifecycle.ChangeAsync(_journal.LoadedPlugins, true, _operation.Token).ConfigureAwait(false);
            Status = "Files restored · Verify names, collections and references";
        }
        catch (Exception ex) { await RecoverFailedRestoreAsync(ex).ConfigureAwait(false); }
        finally { _operation.Dispose(); _operation = null; _operationFinished.TrySetResult(); }
    }
    private async Task RecoverFailedRestoreAsync(Exception failure)
    {
        Error = failure.Message;
        try
        {
            if (_journal != null && !_journal.Consistent)
            {
                if (_journal.Entries.Any(e => e.Applying))
                {
                    await _lifecycle.ChangeAsync(_journal.LoadedPlugins, false, CancellationToken.None).ConfigureAwait(false);
                    await Task.Run(() => _journal.Rollback(JournalPath)).ConfigureAwait(false);
                    _journal.Consistent = false; _journal.RolledBack = true; RolledBack = true;
                    await _journal.VerifyRollbackAsync(SnapshotCapture.HashAsync, CancellationToken.None).ConfigureAwait(false);
                    AwaitingUnload = false; AwaitingReload = true; ManualVerified = false;
                }
                Progress.Phase(SnapshotPhase.Reload);
                await _lifecycle.ChangeAsync(_journal.LoadedPlugins, true, CancellationToken.None).ConfigureAwait(false);
                if (RolledBack) { ClearCompletedRollback(); Error = failure.Message; }
                else if (!_journal.Entries.Any(e => e.Applying))
                { ClearCompletedRollback(); Error = failure.Message; }
                else _journal.Save(JournalPath);
            }
            else Release();
            Progress.Phase(failure is OperationCanceledException ? SnapshotPhase.Cancelled : SnapshotPhase.Failed);
            Status = RolledBack ? "Restore rolled back · Verification required" : "Restore stopped: " + failure.Message;
        }
        catch (Exception recovery)
        {
            Progress.Phase(SnapshotPhase.Failed); Error = failure.Message + " · Recovery: " + recovery.Message;
            Status = "Recovery required · Sync suspended";
            if (_journal != null) { _journal.Consistent = false; _journal.Save(JournalPath); }
        }
    }
    private static async Task<bool> MatchesDocumentAsync(string target, BackupDocumentDto document, CancellationToken ct)
    {
        if (!System.IO.File.Exists(target)) return false;
        if (PersistentDocumentCodec.IsOpaque(document.Json))
            return (await System.IO.File.ReadAllBytesAsync(target, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(PersistentDocumentCodec.DecodeBytes(document.Json));
        return JsonNode.DeepEquals(JsonNode.Parse(await System.IO.File.ReadAllTextAsync(target, ct).ConfigureAwait(false)), JsonNode.Parse(document.Json));
    }
    public async Task FinishAsync()
    {
        if (!AwaitingReload || _journal == null) return;
        if (!ManualVerified) { Error = "Verification required."; Status = "Confirm in-game names, inheritance, automation links and Customize+ references before finishing."; return; }
        if (_pi.InstalledPlugins.Any(p => RequiredPlugins.Contains(p.InternalName) && _journal.LoadedPlugins[p.InternalName] && !p.IsLoaded)) { Error = "Reload required plugins."; Status = "Reload the plugins that were loaded before restore."; return; }
        if (!string.Equals(_backups.ModRoot, ModRoot, StringComparison.OrdinalIgnoreCase)) { Error = "Penumbra mod root mismatch."; Status = "Penumbra mod root differs after reload; correct it before finishing."; return; }
        Progress.Phase(SnapshotPhase.Verifying, _journal.Entries.Count); int verifiedCount = 0;
        var pluginConfigRoot = SnapshotSafety.SourceRoot(_pi.ConfigDirectory.Parent!.FullName);
        foreach (var entry in _journal.Entries)
        {
            var relative = Path.GetRelativePath(pluginConfigRoot, entry.Destination).Replace('\\', '/');
            bool equivalent = !RolledBack && !PluginRestoreVerification.CompareAfterReload(relative)
                || (RolledBack && !entry.Existed ? !System.IO.File.Exists(entry.Destination) : await EquivalentAsync(entry.Destination, RolledBack ? entry.Original : entry.Staged, entry.PluginJson, RolledBack ? "" : relative).ConfigureAwait(false));
            Progress.Advance(++verifiedCount, Path.GetFileName(entry.Destination));
            if (!equivalent) { Error = "Restored file changed: " + Path.GetFileName(entry.Destination); Status = "A restored file changed. Inspect it or roll back; sync remains suspended."; return; }
        }
        Progress.Phase(SnapshotPhase.Reconciling);
        var verifiedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var physicalModRoot = SnapshotSafety.SourceRoot(ModRoot).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var entry in _journal.Entries.Where(e => System.IO.File.Exists(e.Destination) && e.Destination.StartsWith(physicalModRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            verifiedPaths[entry.Destination] = await SnapshotCapture.HashAsync(entry.Destination, CancellationToken.None).ConfigureAwait(false);
        using (SnapshotExclusion.EnterRestoreMutation())
        {
            _cache.ReconcileRestoredPackagePaths(verifiedPaths);
        }
        if (_journal.RepairOperationId is { } repair) await _repair.ReleaseAsync(repair).ConfigureAwait(false);
        _journal.Consistent = true; _journal.Save(JournalPath); AwaitingReload = false; Release(); _mediator.Publish(new EnvironmentRestoreCompletedMessage());
        Progress.Phase(RolledBack ? SnapshotPhase.RolledBack : SnapshotPhase.Complete); Error = null;
        Status = "Restore verified. Assign characters to restored collections; recovery originals remain until discarded.";
    }
    public async Task RollbackAsync()
    {
        if (_journal == null || Busy) return;
        _operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); _operation = new();
        try
        {
            Progress.Phase(SnapshotPhase.Unload);
            await _lifecycle.ChangeAsync(_journal.LoadedPlugins, false, CancellationToken.None).ConfigureAwait(false);
            if (_journal.RolledBack)
            {
                foreach (var entry in _journal.Entries.Where(e => e.Existed || e.ExpectedOriginalHash == null)) entry.Applying = true;
            }
            foreach (var entry in _journal.Entries.Where(e => e.Applying)) SnapshotSafety.VerifyDestination(entry.Destination);
            await Task.Run(() => _journal.Rollback(JournalPath)).ConfigureAwait(false);
            _journal.Consistent = false; _journal.RolledBack = true; _journal.Save(JournalPath);
            RolledBack = true; ManualVerified = false; AwaitingUnload = false; AwaitingReload = true;
            Progress.Phase(SnapshotPhase.Verifying);
            await _journal.VerifyRollbackAsync(SnapshotCapture.HashAsync, CancellationToken.None).ConfigureAwait(false);
            Progress.Phase(SnapshotPhase.Reload);
            await _lifecycle.ChangeAsync(_journal.LoadedPlugins, true, CancellationToken.None).ConfigureAwait(false);
            ClearCompletedRollback();
        }
        catch (Exception ex) { Error = ex.Message; Progress.Phase(SnapshotPhase.Failed); Status = "Rollback failed · Sync suspended"; }
        finally { _operation.Dispose(); _operation = null; _operationFinished.TrySetResult(); }
    }
    private void ClearCompletedRollback()
    {
        if (_journal?.RepairOperationId is { } repair) _ = _repair.ReleaseAsync(repair);
        RepairUnavailable = false;
        if (_journal == null) throw new InvalidOperationException("Recovery journal unavailable.");
        _journal.Consistent = true; _journal.Save(JournalPath);
        Directory.Delete(Path.GetDirectoryName(JournalPath)!, true);
        if (Directory.Exists(RecoveryArchiveRoot)) Directory.Delete(RecoveryArchiveRoot, true);
        _journal = null; _unreadableRecovery = false;
        AwaitingUnload = false; AwaitingReload = false; ManualVerified = false; RolledBack = false;
        Choices = []; CustomizePlusCodes = []; RequiredPlugins = []; Snapshot = Guid.Empty;
        AllowPartial = false; ModRoot = _backups.ModRoot ?? "";
        Error = null; Progress.Reset(); Status = "Ready";
        Release(); _mediator.Publish(new EnvironmentRestoreCompletedMessage());
    }
    public async Task ReloadPluginsAsync()
    {
        if (_journal == null || Busy || !AwaitingReload) return;
        _operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); _operation = new();
        try
        {
            Progress.Phase(SnapshotPhase.Reload);
            await _lifecycle.ChangeAsync(_journal.LoadedPlugins, true, CancellationToken.None).ConfigureAwait(false);
            Error = null; Status = "Plugins re-enabled · Verification required";
        }
        catch (Exception ex) { Error = ex.Message; Progress.Phase(SnapshotPhase.Failed); Status = "Reload failed · Sync suspended"; }
        finally { _operation.Dispose(); _operation = null; _operationFinished.TrySetResult(); }
    }
    public void DiscardRecovery()
    {
        if (_lease != null || Busy || _journal?.Consistent == false) return;
        if (Directory.Exists(Path.GetDirectoryName(JournalPath))) Directory.Delete(Path.GetDirectoryName(JournalPath)!, true);
        if (Directory.Exists(RecoveryArchiveRoot)) Directory.Delete(RecoveryArchiveRoot, true);
        _journal = null;
    }
    private static async Task<bool> EquivalentAsync(string destination, string expected, bool pluginJson, string relative)
    {
        if (!System.IO.File.Exists(destination) || !System.IO.File.Exists(expected)) return false;
        if (await SnapshotCapture.HashAsync(destination, CancellationToken.None).ConfigureAwait(false) == await SnapshotCapture.HashAsync(expected, CancellationToken.None).ConfigureAwait(false)) return true;
        if (!pluginJson) return false;
        try { return PluginRestoreVerification.EquivalentJson(relative, await System.IO.File.ReadAllTextAsync(expected).ConfigureAwait(false), await System.IO.File.ReadAllTextAsync(destination).ConfigureAwait(false)); }
        catch (System.Text.Json.JsonException) { return false; }
    }
    public async ValueTask DisposeAsync()
    {
        _operation?.Cancel();
        if (Busy) await _operationFinished.Task.ConfigureAwait(false);
        _lease?.Dispose(); _lease = null;
    }
    private void Release() { _lease?.Dispose(); _lease = null; _mediator.Publish(new ResumeScanMessage("Environment restore")); }
}
