using System.Collections.Concurrent;
using System.Text.Json;
using Dalamud.Plugin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.FileRepair;
using Snowcloak.Core.FileRepair;
using Snowcloak.Core.EnvironmentSnapshots;
using Snowcloak.EnvironmentSnapshots;
using Snowcloak.FileCache;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Infrastructure.Transfers;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;
namespace Snowcloak.FileRepair;

public sealed class FileRepairService : BackgroundService, IMediatorSubscriber
{
    private readonly ApiController _api;
    private readonly ServerRegistry _servers;
    private readonly FileCacheManager _cache;
    private readonly FileUploadManager _uploads;
    private readonly FileTransferOrchestrator _transfers;
    private readonly FileDownloadNegativeCache _negative;
    private readonly ILogger<FileRepairService> _logger;
    private readonly string _root, _settingsPath;
    private readonly Dictionary<string, bool> _settings;
    private readonly SemaphoreSlim _prepare = new(1), _upload = new(1);
    private readonly ConcurrentDictionary<(string, Guid), (string Path, string Account, DateTime Created)> _staged = new();
    private CancellationTokenSource _donor = new();
    public SnowMediator Mediator { get; }
    private readonly ConcurrentDictionary<Task, byte> _donorTasks = new();
    private CancellationToken _lifetime;
    private DateTime _lastStagingSweep;
    private string _registeredAccount = "";
    private readonly ConcurrentDictionary<Guid, (FileRepairRequest Request, string Account)> _operations = new();
    private readonly ConcurrentDictionary<(Guid, string), Guid> _generations = new();
    public bool Recovering => !_operations.IsEmpty;
    public RepairSourceIndex Sources { get; }
    public string Account => _servers.CurrentApiUrl + "/" + _api.UID;
    public bool Enabled { get { lock (_settings) return _settings.GetValueOrDefault(Account, true); } }
    public bool Available => _api.SupportsFileRepair;
    public FileRepairService(IDalamudPluginInterface pi, ApiController api, ServerRegistry servers, FileCacheManager cache, FileUploadManager uploads, FileTransferOrchestrator transfers, FileDownloadNegativeCache negative, ILogger<FileRepairService> logger, SnowMediator mediator)
    {
        Mediator = mediator;
        _api = api; _servers = servers; _cache = cache; _uploads = uploads; _transfers = transfers; _negative = negative; _logger = logger;
        _root = Path.Combine(pi.ConfigDirectory.FullName, "file-repair-staging"); _settingsPath = Path.Combine(pi.ConfigDirectory.FullName, "file-repair-settings.json");
        Sources = new(Path.Combine(pi.ConfigDirectory.FullName, "file-repair-sources.json"));
        try { _settings = File.Exists(_settingsPath) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_settingsPath)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or JsonException) { _settings = []; }
        _api.FileRepairProbeReceived += ProbeAsync; _api.FileRepairAssignmentReceived += AssignAsync;
        _api.FileRepairStatusReceived += OnStatus;
        mediator.Subscribe<DisconnectedMessage>(this, _ => _donor.Cancel());
        mediator.Subscribe<ConnectionLostMessage>(this, _ => _donor.Cancel());
        mediator.Subscribe<ConnectedMessage>(this, message => { _donor.Cancel(); if (Enabled && Available) ResetDonor(); _ = RegisterQuietlyAsync(); });
    }
    private void ResetDonor()
    {
        _donor.Cancel(); _donor = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _registeredAccount = Account;
    }
    public void RecordSources(string account, IEnumerable<RepairSource> sources)
    {
        try { Sources.Replace(account, sources); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Backup completed; local index could not be persisted"); }
    }
    public void SetEnabled(bool enabled)
    {
        lock (_settings) { _settings[Account] = enabled; File.WriteAllText(_settingsPath + ".tmp", JsonSerializer.Serialize(_settings)); File.Move(_settingsPath + ".tmp", _settingsPath, true); }
        if (!enabled) _donor.Cancel();
        else if (Available) ResetDonor();
        _ = RegisterQuietlyAsync();
    }
    private void OnStatus(FileRepairStatus status)
    {
        if (status.State == FileRepairState.Complete && _operations.TryGetValue(status.OperationId, out var operation)
            && operation.Account == Account && _generations.TryGetValue((status.OperationId, status.Hash), out var generation) && generation == status.Generation)
            _negative.ClearMissing(status.Hash);
    }
    private async Task RegisterQuietlyAsync()
    {
        try { if (Available) await _api.FileRepairRegister(Enabled, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Repair participation registration deferred"); }
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _lifetime = ct;
        while (!ct.IsCancellationRequested)
        {
            if (_registeredAccount != Account || !Enabled || !Available)
            {
                _donor.Cancel();
                if (Enabled && Available) ResetDonor();
                _registeredAccount = Account;
            }
            else if (_donor.IsCancellationRequested) ResetDonor();
            await RegisterQuietlyAsync().ConfigureAwait(false);
            foreach (var (id, op) in _operations)
            {
                if (op.Account != Account || !Available) { _operations.TryRemove(id, out _); continue; }
                try { await _api.FileRepairHeartbeat(id, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Repair lease heartbeat deferred"); }
            }
            foreach (var (key, value) in _staged.Where(p => p.Value.Created.AddMinutes(5) < DateTime.UtcNow || p.Value.Account != Account || !Enabled))
                if (_staged.TryRemove(key, out _)) DeleteStaged(value.Path);
            if (_lastStagingSweep.AddHours(1) < DateTime.UtcNow)
            {
                await Task.Run(SweepAbandonedStaging, ct).ConfigureAwait(false);
                _lastStagingSweep = DateTime.UtcNow;
            }
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
    }
    private void DeleteStaged(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogDebug(ex, "Repair staging cleanup deferred"); }
    }
    private void SweepAbandonedStaging()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var path in Directory.EnumerateFiles(_root).Take(256))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _) || File.GetLastWriteTimeUtc(path).AddDays(1) > DateTime.UtcNow) continue;
                try { using var unused = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogDebug(ex, "Repair staging sweep deferred"); }
    }
    public async Task<bool> TryStageLocalAsync(string hash, string target, CancellationToken ct)
    {
        await _prepare.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                var paths = _cache.GetAllFileCachesByHash(hash, validate: false).Where(c => !c.IsSubstEntry && !string.IsNullOrWhiteSpace(c.ResolvedFilepath)).Select(c => c.ResolvedFilepath).ToList();
                try { paths.AddRange(Sources.Paths(Account, hash)); } catch (Exception ex) when (ex is IOException or InvalidDataException) { }
                foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    try { if (await RepairSourceIndex.StageAsync(path, target, hash, SnapshotCapture.HashAsync, ct).ConfigureAwait(false)) return true; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
                return false;
            }, ct).ConfigureAwait(false);
        }
        finally { _prepare.Release(); }
    }
    private Task ProbeAsync(FileRepairProbe[] probes)
    {
        // Never hold the SignalR callback thread while hashing/staging.
        Track(ProbeInBackgroundAsync(probes)); return Task.CompletedTask;
    }
    private async Task ProbeInBackgroundAsync(FileRepairProbe[] probes)
    {
        if (!Enabled || !Available || probes.Length > 32) return;
        var account = Account;
        var offers = new List<FileRepairOffer>();
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(_donor.Token); ct.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var probe in probes)
            {
                if (probe.Hash.Length != 64 || !probe.Hash.All(char.IsAsciiHexDigit)) continue;
                var offer = new FileRepairOffer { Hash = probe.Hash, Generation = probe.Generation, Availability = FileRepairAvailability.Busy };
                if (!SnapshotExclusion.Blocked && _staged.IsEmpty && _upload.CurrentCount > 0 && _prepare.CurrentCount > 0 && !_transfers.HasForegroundTransferWork && !_uploads.IsUploading)
                {
                    using var admission = SnapshotExclusion.Enter(ct.Token);
                    var path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
                    if (await TryStageLocalAsync(probe.Hash, path, admission.Token).ConfigureAwait(false))
                    {
                        if (_staged.TryAdd((probe.Hash, probe.Generation), (path, account, DateTime.UtcNow))) { offer.Availability = FileRepairAvailability.Available; offer.Size = new FileInfo(path).Length; }
                        else DeleteStaged(path);
                    }
                    else offer.Availability = FileRepairAvailability.Unavailable;
                }
                offers.Add(offer);
            }
            if (Enabled && account == Account) await _api.FileRepairReply(offers, ct.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReleaseProbeFiles(probes);
            if (Enabled && Available && account == Account)
            {
                try { await _api.FileRepairReply(probes.Select(p => new FileRepairOffer { Hash = p.Hash, Generation = p.Generation, Availability = FileRepairAvailability.Busy }).ToList(), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Busy donor reply deferred"); }
            }
        }
        catch (Exception ex) { ReleaseProbeFiles(probes); _logger.LogDebug(ex, "Repair donor probe deferred"); }
    }
    private void ReleaseProbeFiles(FileRepairProbe[] probes)
    {
        foreach (var probe in probes)
            if (_staged.TryRemove((probe.Hash, probe.Generation), out var file)) DeleteStaged(file.Path);
    }
    private void Track(Task task)
    {
        _donorTasks.TryAdd(task, 0);
        _ = task.ContinueWith(t => _donorTasks.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _donor.Cancel();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(_donorTasks.Keys).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private Task AssignAsync(FileRepairAssignment assignment) { Track(UploadInBackgroundAsync(assignment)); return Task.CompletedTask; }
    private async Task UploadInBackgroundAsync(FileRepairAssignment assignment)
    {
        if (!_staged.TryRemove((assignment.Hash, assignment.Generation), out var source)) return;
        string scf = source.Path + ".scf";
        var token = _donor.Token;
        try
        {
            await _upload.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!Enabled || !Available || source.Account != Account) return;
                while (_transfers.HasForegroundTransferWork || _uploads.IsUploading) await Task.Delay(250, token).ConfigureAwait(false);
                using var admission = SnapshotExclusion.Enter(token);
                await _prepare.WaitAsync(admission.Token).ConfigureAwait(false);
                try { await Task.Run(() => SnapshotStagingAdapter.EncodeAsync(source.Path, scf, Sources.Paths(Account, assignment.Hash).Concat(_cache.GetAllFileCachesByHash(assignment.Hash, validate: false).Where(c => !c.IsSubstEntry).Select(c => c.ResolvedFilepath)).Append(source.Path), admission.Token), admission.Token).ConfigureAwait(false); }
                finally { _prepare.Release(); }
                long lastReported = 0; DateTime lastUpdate = DateTime.MinValue;
                var progress = new InlineProgress<UploadProgress>(p =>
                {
                    if (p.Uploaded <= lastReported || DateTime.UtcNow - lastUpdate < TimeSpan.FromSeconds(1)) return;
                    lastReported = p.Uploaded; lastUpdate = DateTime.UtcNow;
                    _ = ReportProgressAsync(assignment, p.Uploaded, false);
                });
                if (!Enabled || !Available || source.Account != Account) throw new OperationCanceledException();
                await _uploads.UploadRepairAsync(assignment, File.OpenRead(scf), progress, admission.Token).ConfigureAwait(false);
                await ReportProgressAsync(assignment, new FileInfo(scf).Length, false).ConfigureAwait(false);
            }
            finally { _upload.Release(); }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Repair donor upload deferred"); await ReportProgressAsync(assignment, 0, true).ConfigureAwait(false); }
        finally { DeleteStaged(source.Path); DeleteStaged(scf); }
    }
    private async Task ReportProgressAsync(FileRepairAssignment a, long bytes, bool failed)
    {
        try { if (Available) await _api.FileRepairProgress(a, bytes, failed, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Repair progress notification deferred"); }
    }
    public async Task ProtectAsync(FileRepairRequest request, CancellationToken ct)
    {
        if (!Available) return;
        _operations[request.OperationId] = (request, Account);
        foreach (var batch in request.Hashes.Chunk(256))
            await _api.FileRepairStart(new() { OperationId = request.OperationId, Context = request.Context, ContextId = request.ContextId, Audience = request.Audience, Hashes = batch, ProtectOnly = true }, ct).ConfigureAwait(false);
    }
    public async Task<List<FileRepairStatus>> WaitAsync(FileRepairRequest request, Action<string>? progress, CancellationToken ct)
    {
        if (!Available) return [];
        _operations.TryAdd(request.OperationId, (request, Account));
        var result = new List<FileRepairStatus>();
        foreach (var batch in request.Hashes.Chunk(256))
        {
            var part = new FileRepairRequest { OperationId = request.OperationId, Context = request.Context, ContextId = request.ContextId, Audience = request.Audience, Hashes = batch };
            var statuses = await _api.FileRepairStart(part, ct).ConfigureAwait(false);
            foreach (var status in statuses) _generations[(request.OperationId, status.Hash)] = status.Generation;
            while (statuses.Any(s => s.State is FileRepairState.Searching or FileRepairState.Uploading))
            {
                progress?.Invoke("Recovering missing files…");
                await Task.Delay(1000, ct).ConfigureAwait(false);
                statuses = (await _api.FileRepairGetStatus(request.OperationId, batch, ct).ConfigureAwait(false)).Where(s => batch.Contains(s.Hash, StringComparer.Ordinal)).ToList();
                if (statuses.Count == 0) throw new IOException("Repair operation expired. Retry the download.");
            }
            foreach (var status in statuses) OnStatus(status);
            result.AddRange(statuses);
        }
        return result;
    }
    public async Task ReleaseAsync(Guid operation)
    {
        _operations.TryRemove(operation, out _);
        foreach (var key in _generations.Keys.Where(k => k.Item1 == operation)) _generations.TryRemove(key, out _);
        try { if (Available) await _api.FileRepairCancel(operation, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Repair operation release deferred; protection will expire"); }
    }
    public override void Dispose()
    {
        _api.FileRepairProbeReceived -= ProbeAsync; _api.FileRepairAssignmentReceived -= AssignAsync; _api.FileRepairStatusReceived -= OnStatus;
        Mediator.UnsubscribeAll(this);
        _donor.Cancel(); base.Dispose();
    }
}
