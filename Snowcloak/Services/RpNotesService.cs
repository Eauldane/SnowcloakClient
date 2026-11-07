using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Snowcloak.API.Dto.Notes;
using Snowcloak.API.Notes;
using Snowcloak.Infrastructure.Data;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI;

namespace Snowcloak.Services;

public sealed class LocalRpNote
{
    public RpNoteRecord Record { get; set; } = new();
    public RpNotesWriteRequest? Pending { get; set; }
    public RpNoteRecord? Conflict { get; set; }
    public List<RpOwnCharacter>? ConflictCharacters { get; set; }
    public bool HasConflict { get; set; }
    public bool HasNextDraft { get; set; }
    public RpDossier? NextDraft { get; set; }
    public List<RpOwnCharacter>? NextCharacters { get; set; }
    public Guid NextCharactersVersion { get; set; }
    public string Error { get; set; } = "";
    public RpNotesPage? Metadata { get; set; }
}
public sealed record RpNotesView(string Scope, string Owner, string Service, RpNotesPage Metadata,
    IReadOnlyList<LocalRpNote> Notes, string Status, long Revision, bool Complete);

public sealed class RpNotesService : BackgroundService
{
    private readonly ApiController _api;
    private readonly ServerRegistry _servers;
    private readonly string _directory;
    private RpNotesCache? _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private long _resetEpoch;
    private readonly Dictionary<string, LocalRpNote> _notes = new(StringComparer.Ordinal);
    private RpNotesPage _metadata = new();
    private RpNotesWriteRequest? _batch;
    private string _scope = "", _owner = "", _server = "", _status = "Connect to load your notes";
    private string? _priorityTarget;
    private DateTime _refreshAt;
    private DateTime _retryAt;
    private int _failures;
    private long _revision;
    private bool _complete;
    private string[] _cachedOwners = [];
    public string[] CachedOwners => Volatile.Read(ref _cachedOwners);
    private RpNotesView _view = new("", "", "", new(), [], "Connect to load your notes", 0, false);
    public RpNotesView View => Volatile.Read(ref _view);
    public bool IsConnected => _api.IsConnected;

    public Task<Dictionary<string, string>?> LoadProfiles(string scope) => CommandResult<Dictionary<string, string>?>(async () =>
    {
        if (scope != _scope || !Authenticated())
            return null;
        return await Network(_api.RpNotesOwnProfiles);
    });
    public bool HasRpNote(string target)
    {
        var view = View;
        return IsCurrent(view) && view.Notes.Any(x => x.Record.TargetUid == target &&
            (x.HasNextDraft ? x.NextDraft != null : x.Pending != null ? x.Pending.Changes[0].Dossier != null : x.Record.Dossier != null));
    }
    public bool IsCurrent(RpNotesView view) => view.Service == _servers.CurrentApiUrl
        && (!_api.IsConnected || view.Owner == _api.UID);
    public RpNotesService(ApiController api, ServerRegistry servers, Snowcloak.Configuration.SnowcloakConfigService config)
    { _api = api; _servers = servers; _directory = config.ConfigurationDirectory; }

    public void Open(string target) => Command(async () => {
        if (!RpNotesPolicy.ValidUid(target)) return;
        _priorityTarget = target; _refreshAt = DateTime.MinValue; _retryAt = DateTime.MinValue;
        if (_scope.Length > 0 && !_notes.ContainsKey(target)) _status = "Loading...";
        Publish(); await Task.CompletedTask;
    });
    public void Retry(string scope, string target) => Command(async () => {
        if (scope != _scope || !_notes.TryGetValue(target, out var entry) || entry.Pending == null || entry.HasConflict) return;
        var own = entry.Pending.OwnCharacters ?? _metadata.OwnCharacters;
        var dossier = entry.Pending.Changes[0].Dossier;
        if (!RpNotesPolicy.ValidCharacters(own) || (dossier != null && !RpNotesPolicy.ValidDossier(dossier, own.Select(x => x.Id).ToHashSet()))) return;
        entry.Error = ""; Cache().Save(_scope, target, entry); _retryAt = DateTime.MinValue; Publish(); await Task.CompletedTask;
    });

    public void SelectOfflineOwner(string owner) => Command(async () => {
        if (_api.IsConnected || !CachedOwners.Contains(owner, StringComparer.Ordinal)) return;
        _owner = owner; _server = _servers.CurrentApiUrl; _scope = JsonSerializer.Serialize(new[] { _server, owner });
        _notes.Clear(); _metadata = new(); _batch = null;
        foreach (var row in Cache().Read<LocalRpNote>(_scope)) {
            if (row.Key == "\u0001metadata") _metadata = row.Value.Metadata ?? new();
            else if (row.Key == "\u0001batch") _batch = row.Value.Pending;
            else _notes[row.Key] = row.Value;
        }
        _complete = false; _status = "Offline - showing local copies"; Publish(); await Task.CompletedTask;
    });

    public Task<bool> Save(string scope, string target, RpDossier? dossier, List<RpOwnCharacter> own, Guid? expectedCharactersVersion = null) => CommandResult(async () => {
        if (scope != _scope) {
            var rows = Cache().Read<LocalRpNote>(scope);
            var meta = rows.GetValueOrDefault("\u0001metadata")?.Metadata;
            if (meta == null || !rows.TryGetValue(target, out var old)) return false;
            if (old.Pending != null) { old.HasNextDraft = true; old.NextDraft = dossier; old.NextCharacters = own; old.NextCharactersVersion = expectedCharactersVersion ?? meta.CharactersVersion; }
            else old.Pending = new() { SupportsCharacterJournal = true, Generation = meta.Generation, ExpectedCharactersVersion = expectedCharactersVersion ?? meta.CharactersVersion,
                OwnCharacters = own, Changes = [new() { TargetUid = target, ExpectedVersion = old.Record.Version, Dossier = dossier }] };
            Cache().Save(scope, target, old); return true;
        }
        if (_metadata.Generation == Guid.Empty) return false;
        if (_batch != null) { _status = "Import is waiting for confirmation"; Publish(); return false; }
        var valid = RpNotesPolicy.ValidCharacters(own) && (dossier == null || RpNotesPolicy.ValidDossier(dossier, own.Select(x => x.Id).ToHashSet()));
        var validationError = valid ? "" : "fix invalid date, oversized field, tag limit or character reference before sync.";
        if (!_notes.TryGetValue(target, out var entry))
        { _status = "Load the server copy before editing."; Publish(); return false; }
        if (entry.Pending != null && entry.Error == "")
        {
            var queued = RpNotesPolicy.Clone(entry);
            queued.HasNextDraft = true; queued.NextDraft = RpNotesPolicy.Clone(dossier);
            queued.NextCharacters = RpNotesPolicy.Clone(own);
            queued.NextCharactersVersion = expectedCharactersVersion ?? _metadata.CharactersVersion;
            Cache().Save(_scope, target, queued); _notes[target] = queued;
            _status = "Saved locally - waiting to sync"; Publish(); return true;
        }
        var request = new RpNotesWriteRequest {
            SupportsCharacterJournal = true,
            Generation = _metadata.Generation,
            Changes = [new() { TargetUid = target, ExpectedVersion = entry.Record.Version, Dossier = RpNotesPolicy.Clone(dossier) }],
            OwnCharacters = JsonSerializer.Serialize(own) == JsonSerializer.Serialize(_metadata.OwnCharacters) ? null : RpNotesPolicy.Clone(own),
            ExpectedCharactersVersion = expectedCharactersVersion ?? _metadata.CharactersVersion
        };
        var updated = RpNotesPolicy.Clone(entry); updated.Pending = request; updated.Error = validationError;
        updated.HasNextDraft = false; updated.NextDraft = null; updated.NextCharacters = null;
        Cache().Save(_scope, target, updated); // FULL synchronous commit before publishing local success
        _notes[target] = updated;
        _status = validationError.Length > 0 ? validationError : "Saved locally - waiting to sync"; Publish();
        await Task.CompletedTask; return true;
    });

    public void Resolve(string scope, string target, bool keepMine) => Command(async () => {
        if (scope != _scope || !_notes.TryGetValue(target, out var entry) || !entry.HasConflict) return;
        if (!Authenticated()) return;
        var page = await Network(() => _api.RpNotesRead(new() { TargetUid = target }));
        entry = _notes[target];
        var remote = page.Records.SingleOrDefault() ?? new RpNoteRecord { TargetUid = target };
        if (keepMine && (entry.Conflict == null || entry.Conflict.Version != remote.Version || entry.ConflictCharacters == null
            || JsonSerializer.Serialize(entry.ConflictCharacters) != JsonSerializer.Serialize(page.OwnCharacters))) {
            entry.Conflict = remote; entry.ConflictCharacters = page.OwnCharacters; Cache().Save(_scope, target, entry);
            _status = "Server version loaded or changed again, please reload"; Publish(); return;
        }
        if (keepMine && page.Generation != _metadata.Generation)
        { _status = "Notebook was reset."; Publish(); return; }
        var updated = RpNotesPolicy.Clone(entry);
        if (keepMine && updated.Pending != null) {
            if (updated.HasNextDraft) {
                updated.Pending.Changes[0].Dossier = updated.NextDraft;
                updated.Pending.OwnCharacters = updated.NextCharacters;
            }
            updated.Pending = RpNotesPolicy.Clone(updated.Pending);
            updated.Pending.MutationId = Guid.NewGuid(); updated.Pending.Generation = page.Generation;
            updated.Pending.SupportsCharacterJournal = true;
            updated.Pending.Changes[0].ExpectedVersion = remote.Version;
            updated.Pending.ExpectedCharactersVersion = page.CharactersVersion;
            if (updated.Pending.OwnCharacters != null) {
                var merged = RpNotesPolicy.Clone(page.OwnCharacters);
                foreach (var own in updated.Pending.OwnCharacters) {
                    var index = merged.FindIndex(x => x.Id == own.Id);
                    if (index < 0) merged.Add(own); else merged[index] = own;
                }
                updated.Pending.OwnCharacters = merged;
            }
        } else updated.Pending = null;
        updated.HasNextDraft = false; updated.NextDraft = null; updated.NextCharacters = null;
        updated.Record = remote; updated.HasConflict = false; updated.Conflict = null; updated.Error = "";
        Cache().Save(_scope, target, updated); _notes[target] = updated;
        SetMetadata(page); _status = keepMine ? "Saved locally - waiting to sync" : "Using server version"; Publish();
    });

    public Task<bool> ClearCache(string scope) => CommandResult(async () => {
        if (scope != _scope || _batch != null || _notes.Values.Any(x => x.Pending != null)) {
            _status = "Sync pending changes before clearing local copies."; Publish(); return false;
        }
        Cache().Delete(_scope); _notes.Clear(); _metadata = new(); _complete = false; _refreshAt = DateTime.MinValue;
        Publish(); await Task.CompletedTask; return true;
    });
    public Task<bool> Reset(string scope) => CommandResult(async () => {
        if (scope != _scope || !Authenticated()) return false;
        var request = new RpNotesWriteRequest { Generation = _metadata.Generation, Reset = true };
        var response = await Network(() => _api.RpNotesWrite(request));
        if (response.Status != RpNotesWriteStatus.Saved) { _status = "Notebook changed. Refresh and retry."; Publish(); return false; }
        _resetEpoch++; Cache().Delete(_scope); _notes.Clear(); _metadata = new(); _batch = null; _complete = false; _refreshAt = DateTime.MinValue; Publish(); return true;
    });

    public Task<bool> Export(string scope, string path, bool includePending, string? target = null) => CommandResult(async () => {
        if (scope != _scope) return false;
        var records = _notes.Values.Where(x => target == null || x.Record.TargetUid == target).Select(x => {
            var record = RpNotesPolicy.Clone(x.Record);
            if (includePending && x.HasNextDraft) record.Dossier = x.NextDraft;
            else if (includePending && x.Pending != null) record.Dossier = x.Pending.Changes[0].Dossier;
            return record;
        }).Where(x => x.Dossier != null).ToList();
        var own = RpNotesPolicy.Clone(_metadata.OwnCharacters);
        if (includePending) foreach (var entry in _notes.Values) {
            foreach (var character in entry.NextCharacters ?? entry.Pending?.OwnCharacters ?? []) {
                var index = own.FindIndex(x => x.Id == character.Id);
                if (index < 0) own.Add(character); else own[index] = character;
            }
        }
        var export = new RpNotesExport { Service = _server, OwnerUid = _owner, OwnCharacters = own,
            Records = records, ExportedAtUtc = DateTime.UtcNow };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
        _status = _complete ? "Notebook exported" : "Downloaded records exported (sync incomplete)"; Publish(); return true;
    });

    public Task<RpNotesExport?> ReadImport(string scope, string path) => CommandResult<RpNotesExport?>(async () => {
        if (scope != _scope || !Authenticated()) return null;
        if (new FileInfo(path).Length > RpNotesPolicy.MaxNotebookBytes) throw new InvalidDataException();
        var file = JsonSerializer.Deserialize<RpNotesExport>(await File.ReadAllTextAsync(path));
        if (file == null || file.FormatVersion != 1 || file.Service != _server || file.OwnerUid != _owner
            || !RpNotesPolicy.ValidCharacters(file.OwnCharacters) || file.Records == null
            || file.Records.Count > RpNotesPolicy.MaxDossiers
            || file.Records.Select(x => x.TargetUid).Distinct(StringComparer.Ordinal).Count() != file.Records.Count
            || file.Records.Any(x => !RpNotesPolicy.ValidUid(x.TargetUid) || !RpNotesPolicy.ValidDossier(x.Dossier,
                file.OwnCharacters.Select(c => c.Id).ToHashSet()))) throw new InvalidDataException();
        return file;
    });
    public Task<bool> Import(string scope, RpNotesExport file, bool replace, Guid expectedGeneration,
        Guid expectedCharactersVersion, Dictionary<string, Guid> expectedVersions) => CommandResult(async () => {
        if (scope != _scope || !Authenticated() || _batch != null || _notes.Values.Any(x => x.Pending != null)) return false;
        var own = RpNotesPolicy.Clone(_metadata.OwnCharacters);
        foreach (var character in file.OwnCharacters)
        {
            var existing = own.FindIndex(x => x.Id == character.Id);
            if (existing < 0) own.Add(character);
            else if (replace) own[existing] = character;
            else if (JsonSerializer.Serialize(own[existing]) != JsonSerializer.Serialize(character))
            { _status = "Perspective definition conflict: review replacement or cancel import."; Publish(); return false; }
        }
        var changes = file.Records.Where(x => replace || !_notes.TryGetValue(x.TargetUid, out var entry) || entry.Record.Dossier == null)
            .Select(x => new RpNoteChange { TargetUid = x.TargetUid, Dossier = x.Dossier,
                ExpectedVersion = expectedVersions.GetValueOrDefault(x.TargetUid) }).ToList();
        var request = new RpNotesWriteRequest { SupportsCharacterJournal = true, Generation = expectedGeneration, ExpectedCharactersVersion = expectedCharactersVersion,
            OwnCharacters = own, Changes = changes };
        Cache().Save(_scope, "\u0001batch", new LocalRpNote { Pending = request }); _batch = request;
        return await UploadBatch();
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
            try {
                if (_servers.CurrentApiUrl != _server || (_api.IsConnected && _owner != _api.UID)) {
                    _server = _servers.CurrentApiUrl; _owner = _api.IsConnected ? _api.UID : ""; _scope = "";
                    _notes.Clear(); _metadata = new(); _batch = null; _complete = false; _refreshAt = DateTime.MinValue;
                    Volatile.Write(ref _cachedOwners, Cache().Scopes().Select(x => JsonSerializer.Deserialize<string[]>(x)!)
                        .Where(x => x.Length == 2 && x[0] == _server).Select(x => x[1]).Distinct(StringComparer.Ordinal).ToArray());
                    if (_owner.Length > 0) {
                        _scope = JsonSerializer.Serialize(new[] { _server, _owner });
                        foreach (var row in Cache().Read<LocalRpNote>(_scope)) {
                            if (row.Key == "\u0001metadata") _metadata = row.Value.Metadata ?? new();
                            else if (row.Key == "\u0001batch") _batch = row.Value.Pending;
                            else _notes[row.Key] = row.Value;
                        }
                    }
                    Publish();
                }
                if (!Authenticated()) {
                    var status = _api.IsConnected ? "Loading notebook..." : "Offline - showing local copies";
                    if (_status != status) { _status = status; Publish(); }
                } else if (DateTime.UtcNow >= _retryAt) {
                    if (_batch != null) await UploadBatch();
                    if (_priorityTarget != null) {
                        var target = _priorityTarget; _priorityTarget = null;
                        if (_notes.TryGetValue(target, out var priority) && priority.Pending != null && !priority.HasConflict && priority.Error == "") await Upload(target);
                        await RefreshTarget(target);
                    }
                    foreach (var target in _notes.Where(x => x.Value.Pending != null && !x.Value.HasConflict && x.Value.Error == "").Select(x => x.Key).ToArray())
                        await Upload(target);
                    if (DateTime.UtcNow >= _refreshAt) {
                        await RefreshAll(); _refreshAt = DateTime.UtcNow.AddSeconds(30);
                    }
                    _failures = 0;
                }
            } catch (Exception) {
                _status = "Could not refresh or sync notes - local drafts retained. Retry when connected.";
                _retryAt = DateTime.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(++_failures, 6))) + Random.Shared.NextDouble());
                _refreshAt = _retryAt; Publish();
            } finally { _gate.Release(); }
            await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        }
    }

    private bool Authenticated() => _api.IsConnected && _api.UID == _owner && _servers.CurrentApiUrl == _server && _scope.Length > 0;
    private RpNotesCache Cache() => _cache ??= new RpNotesCache(_directory);
    private void SetMetadata(RpNotesPage page)
    {
        if (page.Generation == _metadata.Generation && page.SnapshotVersion < _metadata.SnapshotVersion) return;
        _metadata = new() { Generation = page.Generation, CharactersVersion = page.CharactersVersion,
            OwnCharacters = page.OwnCharacters, SnapshotVersion = page.SnapshotVersion };
        Cache().Save(_scope, "\u0001metadata", new LocalRpNote { Metadata = _metadata });
    }
    private async Task RefreshTarget(string target)
    {
        if (!Authenticated()) return;
        var page = await Network(() => _api.RpNotesRead(new() { TargetUid = target }));
        if (page.Generation == _metadata.Generation && page.SnapshotVersion < _metadata.SnapshotVersion) return;
        ApplyRemote(page.Records.SingleOrDefault() ?? new() { TargetUid = target }, page.Generation);
        SetMetadata(page); _status = "Server copy checked"; Publish();
    }
    private void ApplyRemote(RpNoteRecord remote, Guid generation)
    {
        if (_notes.TryGetValue(remote.TargetUid, out var entry) && entry.Pending != null) {
            if (entry.Pending.Generation != generation || entry.Pending.Changes[0].ExpectedVersion != remote.Version) {
                entry.HasConflict = true; entry.Conflict = remote;
            }
        } else _notes[remote.TargetUid] = new() { Record = remote };
        Cache().Save(_scope, remote.TargetUid, _notes[remote.TargetUid]);
    }
    private async Task RefreshAll()
    {
        var records = new List<RpNoteRecord>(); string after = ""; long? version = null;
        RpNotesPage? last = null;
        do {
            if (!Authenticated()) return;
            var request = new RpNotesReadRequest { AfterUid = after, SnapshotVersion = version };
            var page = await Network(() => _api.RpNotesRead(request));
            if (page.RestartRequired) { _complete = false; _status = "Notebook changed during download - retrying"; Publish(); return; }
            version ??= page.SnapshotVersion; last = page; records.AddRange(page.Records); after = page.NextAfterUid ?? "";
            if (_priorityTarget != null) { var target = _priorityTarget; _priorityTarget = null; await RefreshTarget(target); }
            if (after.Length > 0) { _gate.Release(); try { await Task.Delay(300); } finally { await _gate.WaitAsync(); } }
        } while (after.Length > 0);
        if (last == null) return;
        if (last.Generation == _metadata.Generation && last.SnapshotVersion < _metadata.SnapshotVersion) return;
        foreach (var record in records) ApplyRemote(record, last.Generation);
        foreach (var target in _notes.Keys.Except(records.Select(x => x.TargetUid), StringComparer.Ordinal).ToArray())
            ApplyRemote(new() { TargetUid = target }, last.Generation);
        SetMetadata(last); _complete = true; _status = "Notebook synced"; Publish();
    }
    private async Task Upload(string target)
    {
        if (!Authenticated()) return;
        var entry = _notes[target]; var request = entry.Pending!;
        var baseOwn = RpNotesPolicy.Clone(request.OwnCharacters ?? _metadata.OwnCharacters);
        _status = "Syncing..."; Publish();
        var response = await Network(() => _api.RpNotesWrite(request));
        if (response.Status == RpNotesWriteStatus.Saved) {
            var page = await Network(() => _api.RpNotesRead(new() { TargetUid = target }));
            entry = _notes[target];
            if (page.Generation != request.Generation) {
                entry.HasConflict = true; entry.Conflict = page.Records.SingleOrDefault() ?? new() { TargetUid = target };
                entry.ConflictCharacters = page.OwnCharacters;
                Cache().Save(_scope, target, entry); SetMetadata(page); Publish(); return;
            }
            var record = response.Records.Single();
            record.Dossier = request.Changes[0].Dossier;
            var current = page.Records.SingleOrDefault() ?? new RpNoteRecord { TargetUid = target };
            var saved = new LocalRpNote { Record = entry.HasNextDraft ? record : current };
            if (entry.HasNextDraft) saved.Pending = new() {
                SupportsCharacterJournal = true,
                Generation = response.Generation,
                ExpectedCharactersVersion = request.OwnCharacters != null && page.CharactersVersion == response.CharactersVersion
                    && entry.NextCharactersVersion == request.ExpectedCharactersVersion ? response.CharactersVersion : entry.NextCharactersVersion,
                OwnCharacters = JsonSerializer.Serialize(entry.NextCharacters) == JsonSerializer.Serialize(baseOwn) ? null : entry.NextCharacters,
                Changes = [new() { TargetUid = target, ExpectedVersion = record.Version, Dossier = entry.NextDraft }] };
            if (entry.HasNextDraft && current.Version != record.Version) {
                saved.HasConflict = true; saved.Conflict = current; saved.ConflictCharacters = page.OwnCharacters;
            }
            Cache().Save(_scope, target, saved); _notes[target] = saved;
            SetMetadata(page);
            _status = "Saved to server";
        } else if (response.Status is RpNotesWriteStatus.Conflict or RpNotesWriteStatus.RetryExpired) {
            entry = _notes[target];
            entry.HasConflict = true;
            entry.Conflict = response.Records.FirstOrDefault(x => x.TargetUid == target);
            Cache().Save(_scope, target, entry); _status = "Conflict - review changes";
        } else {
            entry = _notes[target];
            entry.Error = response.Status == RpNotesWriteStatus.QuotaExceeded ? "Notebook quota exceeded" : "Invalid note or perspective references";
            Cache().Save(_scope, target, entry); _status = entry.Error;
        }
        Publish();
    }
    private async Task<bool> UploadBatch()
    {
        var request = _batch!;
        var response = await Network(() => _api.RpNotesWrite(request));
        if (response.Status == RpNotesWriteStatus.Saved) {
            var page = await Network(() => _api.RpNotesRead(new()));
            foreach (var record in response.Records) {
                record.Dossier = request.Changes.Single(x => x.TargetUid == record.TargetUid).Dossier;
                var saved = new LocalRpNote { Record = record };
                Cache().Save(_scope, record.TargetUid, saved); _notes[record.TargetUid] = saved;
            }
            SetMetadata(page);
            _status = "Import saved to server";
        } else _status = "Import rejected or conflicted; refresh and preview again. No partial server changes.";
        Cache().Save(_scope, "\u0001batch", new LocalRpNote()); _batch = null;
        _refreshAt = DateTime.MinValue; Publish(); return response.Status == RpNotesWriteStatus.Saved;
    }
    private void Publish()
    {
        var notes = RpNotesPolicy.Clone(_notes.Values.ToList());
        var metadata = RpNotesPolicy.Clone(_metadata);
        foreach (var entry in notes) foreach (var own in entry.NextCharacters ?? entry.Pending?.OwnCharacters ?? []) {
            var index = metadata.OwnCharacters.FindIndex(x => x.Id == own.Id);
            if (index < 0) metadata.OwnCharacters.Add(own); else metadata.OwnCharacters[index] = own;
        }
        Volatile.Write(ref _view, new(_scope, _owner, _server, metadata, notes, _status, ++_revision, _complete));
    }
    private async Task<T> Network<T>(Func<Task<T>> action)
    {
        var scope = _scope; var owner = _owner; var server = _server; var epoch = _resetEpoch;
        T result;
        _gate.Release();
        try {
            await _networkGate.WaitAsync();
            try {
                if (!_api.IsConnected || _api.UID != owner || _servers.CurrentApiUrl != server)
                    throw new OperationCanceledException();
                result = await action();
            } finally { _networkGate.Release(); }
        } finally { await _gate.WaitAsync(); }
        if (scope != _scope || epoch != _resetEpoch) throw new OperationCanceledException();
        return result;
    }
    private void Command(Func<Task> action) => _ = CommandResult(async () => { await action(); return true; });
    private Task<T> CommandResult<T>(Func<Task<T>> action) => Task.Run(async () => {
        await _gate.WaitAsync();
        try { return await action(); }
        catch (Exception) { _status = "Operation failed - text retained; check connection, file or storage and retry."; Publish(); return default!; }
        finally { _gate.Release(); }
    });
}
