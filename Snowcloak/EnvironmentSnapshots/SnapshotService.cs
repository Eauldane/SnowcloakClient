using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin;
using Microsoft.Extensions.Hosting;
using Snowcloak.API.Dto.Backups;
using Snowcloak.CacheFile;
using Snowcloak.CacheFile.Enums;
using Snowcloak.Core.EnvironmentSnapshots;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Services.Mediator;
using Snowcloak.Configuration.Models;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.WebAPI.SignalR;

namespace Snowcloak.EnvironmentSnapshots;

public sealed class SnapshotService : BackgroundService
{
    private readonly IDalamudPluginInterface _pi;
    private readonly IpcCallerPenumbra _penumbra;
    private readonly ServerRegistry _servers;
    private readonly ApiController _api;
    private readonly TokenProvider _tokens;
    private readonly FileTransferOrchestrator _transfers;
    private readonly FileUploadManager _uploads;
    private readonly Snowcloak.FileRepair.FileRepairService _repair;
    private readonly SnowMediator _mediator;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _serial = new(1);
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, DailySnapshotState> _states;
    private CancellationTokenSource? _operation;
    private string? _activeServerKey;
    private volatile bool _paused;
    private volatile bool _automaticOperation;
    public string Status { get; private set; } = "Ready. Initiate and review your first backup.";
    public bool Busy => _operation != null;
    public bool Paused => _paused;
    public SnapshotProgress Progress { get; } = new();
    public string? Error { get; private set; }
    public bool WaitingForSync { get; private set; }
    public long ContentBytes => Progress.Read().ContentBytes;
    public long ReusedBytes => Progress.Read().ReusedBytes;
    public long UploadedBytes => Progress.Read().SentBytes;
    public string? UnavailableReason => !_api.IsConnected ? "Offline" : !_api.SupportsEnvironmentBackups ? "Server update required" : !_penumbra.APIAvailable ? "Penumbra unavailable" : null;
    public string? ModRoot => _penumbra.ModDirectory;
    public List<BackupSummaryDto> Snapshots { get; private set; } = [];
    private string Key => _servers.CurrentApiUrl + "/" + _api.UID;
    private string StatePath => Path.Combine(_pi.ConfigDirectory.FullName, "environment-backup-schedule.json");
    public DailySnapshotState Current
    {
        get { lock (_states) { if (!_states.TryGetValue(Key, out var state)) _states[Key] = state = new(); return state; } }
    }
    public SnapshotService(IDalamudPluginInterface pi, IpcCallerPenumbra penumbra, ServerRegistry servers, ApiController api,
        TokenProvider tokens, FileTransferOrchestrator transfers, FileUploadManager uploads, SnowMediator mediator, Snowcloak.FileRepair.FileRepairService repair)
    {
        _repair = repair;
        _pi = pi; _penumbra = penumbra; _servers = servers; _api = api; _tokens = tokens; _transfers = transfers; _uploads = uploads; _mediator = mediator;
        _states = File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, DailySnapshotState>>(File.ReadAllText(StatePath)) ?? [] : [];
    }
    public string[] InstalledPlugins() => _pi.InstalledPlugins.Where(p => p.InternalName is "Penumbra" or "Glamourer" or "CustomizePlus").Select(p => p.InternalName).ToArray();
    public Task<string[]> InstalledModsAsync() => Task.Run(InstalledMods);
    public string[] InstalledMods() => ModRoot == null || !Directory.Exists(ModRoot) ? [] : Directory.GetDirectories(ModRoot).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public void SetAutomatic(bool enabled) { Current.Enabled = enabled && Current.Snapshot.HasValue; if (!enabled && _automaticOperation) Cancel(); SaveState(); }
    public void TogglePause() => _paused = !_paused;
    public void Cancel() { try { _operation?.Cancel(); } catch (ObjectDisposedException) { } }
    private void SaveState()
    {
        lock (_states) { Directory.CreateDirectory(_pi.ConfigDirectory.FullName); File.WriteAllText(StatePath + ".tmp", JsonSerializer.Serialize(_states)); File.Move(StatePath + ".tmp", StatePath, true); }
    }
    private async Task YieldAsync(CancellationToken ct)
    {
        try
        {
            while (_paused || _uploads.IsUploading || _transfers.HasForegroundTransferWork)
            {
                WaitingForSync = _uploads.IsUploading || _transfers.HasForegroundTransferWork;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        finally { WaitingForSync = false; }
        await Task.Yield();
    }
    private async Task WaitForPauseAsync(CancellationToken ct)
    {
        while (_paused) await Task.Delay(100, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }
    private sealed class UploadReporter(SnapshotProgress progress) : IProgress<UploadProgress>
    { public void Report(UploadProgress value) => progress.Transfer(value.Uploaded, value.Size); }
    public async Task DeleteAsync(Guid id)
    {
        try
        {
            using var admission = SnapshotExclusion.Enter();
            await RequestAsync<object>(HttpMethod.Delete, $"backups/{id}", null, false, admission.Token).ConfigureAwait(false);
            if (Current.Snapshot == id) { Current.Enabled = false; Current.Snapshot = null; SaveState(); }
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { Error = ex.Message; Status = "Backup deletion failed: " + ex.Message; }
    }
    public async Task RefreshAsync()
    {
        try
        {
            var all = new List<BackupSummaryDto>();
            for (int offset = 0; ; offset += 100)
            {
                var page = await RequestAsync<List<BackupSummaryDto>>(HttpMethod.Get, "backups?offset=" + offset, null, false, CancellationToken.None).ConfigureAwait(false);
                all.AddRange(page); if (page.Count < 100) break;
            }
            Snapshots = all; Error = null;
        }
        catch (Exception ex) { Error = ex.Message; Status = ex.Message; }
    }
    public Task BackupAsync(string[] plugins, bool automaticAfterSuccess) => RunAsync(plugins, automaticAfterSuccess, false, CancellationToken.None);
    private async Task RunAsync(string[] plugins, bool automaticAfterSuccess, bool automatic, CancellationToken stopping)
    {
        if (!await _serial.WaitAsync(0, stopping).ConfigureAwait(false)) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stopping); _operation = operation; _automaticOperation = automatic;
        Error = null; Progress.Reset(); Progress.Phase(SnapshotPhase.Enumerating);
        var state = Current; var serverKey = Key; _activeServerKey = serverKey; var ct = operation.Token;
        try
        {
            using var restoreAdmission = SnapshotExclusion.Enter(ct);
            ct = restoreAdmission.Token;
            if (!_api.IsConnected) throw new InvalidOperationException("Connect to Snowcloak before backing up.");
            if (!_api.SupportsEnvironmentBackups) throw new InvalidOperationException("This server does not advertise environment backup support. The server must be updated before backups can run.");
            if (!_penumbra.APIAvailable || !_pi.InstalledPlugins.Any(p => p.InternalName == "Penumbra" && p.IsLoaded)) throw new InvalidOperationException("Load Penumbra before backing up.");
            var root = ModRoot ?? throw new DirectoryNotFoundException("Penumbra mod root is unavailable.");
            Status = "Capturing stable contents…";
            var mods = await Task.Run(InstalledMods, ct).ConfigureAwait(false);
            var pluginRoots = new Dictionary<string, (string Root, string Version)>();
            var pluginMetadata = _pi.InstalledPlugins.Where(p => plugins.Contains(p.InternalName)).Select(p => (p.InternalName, Version: p.Version.ToString(), p.IsLoaded)).OrderBy(p => p.InternalName, StringComparer.Ordinal).ToArray();
            foreach (var plugin in _pi.InstalledPlugins.Where(p => plugins.Contains(p.InternalName)))
            {
                var path = Path.Combine(_pi.ConfigDirectory.Parent!.FullName, plugin.InternalName);
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Cannot locate persistent settings for " + plugin.InternalName);
                pluginRoots[plugin.InternalName] = (path, plugin.Version.ToString());
            }
            if (pluginMetadata.Any(p => !p.IsLoaded) || plugins.Any(p => !pluginRoots.ContainsKey(p))) throw new InvalidOperationException("A selected plugin is unavailable.");
            using var capture = await SnapshotCapture.CaptureAsync(root, mods, pluginRoots, Path.Combine(_pi.ConfigDirectory.FullName, "backup-staging"), ct, YieldAsync, update =>
            {
                if (Progress.Read().Phase != update.Phase || Progress.Read().Attempt != update.Attempt)
                    Progress.Phase(update.Phase, update.Total, attempt: update.Attempt);
                Progress.Advance(update.Done, update.Path);
            }).ConfigureAwait(false);
            if (!pluginMetadata.SequenceEqual(_pi.InstalledPlugins.Where(p => plugins.Contains(p.InternalName)).Select(p => (p.InternalName, Version: p.Version.ToString(), p.IsLoaded)).OrderBy(p => p.InternalName, StringComparer.Ordinal)) || !string.Equals(root, ModRoot, StringComparison.Ordinal)) throw new IOException("Plugin inventory changed during capture.");
            if (serverKey != Key) throw new InvalidOperationException("Account or server changed during capture.");
            Progress.Inventory(capture.Files.Count, capture.Files.Sum(f => f.Size), capture.Files.Select(f => f.Hash).Distinct().Count());
            Progress.Phase(SnapshotPhase.Submitting, capture.Files.Count + capture.Documents.Count);
            int submitted = 0;
            BackupSummaryDto? previous = null;
            if (state.Snapshot.HasValue)
            {
                try
                {
                    previous = await RequestAsync<BackupSummaryDto>(HttpMethod.Get, $"backups/{state.Snapshot}/summary", null, false, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                }
            }
            bool renew = previous?.Complete == true && previous.ContentHash == capture.ContentHash;
            Guid id = renew ? previous!.Id : Guid.NewGuid();
            if (!renew)
            {
                await RequestAsync<BackupSummaryDto>(HttpMethod.Post, "backups", new BackupCreateDto { Id = id, Label = "Environment backup", ContentHash = capture.ContentHash, ExpectedFiles = capture.Files.Count, ExpectedDocuments = capture.Documents.Count }, false, ct).ConfigureAwait(false);
                foreach (var page in capture.Files.Chunk(256))
                {
                    await YieldAsync(ct).ConfigureAwait(false);
                    await RequestAsync<object>(HttpMethod.Put, $"backups/{id}/files", page, false, ct).ConfigureAwait(false);
                    Progress.Advance(submitted += page.Length);
                }
                foreach (var document in capture.Documents)
                {
                    byte[] data = Encoding.UTF8.GetBytes(document.Json); var chunks = data.Chunk(512 * 1024).ToArray();
                    for (int i = 0; i < chunks.Length; i++)
                    {
                        await YieldAsync(ct).ConfigureAwait(false);
                        await RequestAsync<object>(HttpMethod.Put, $"backups/{id}/document", new BackupDocumentPartDto { Plugin = document.Plugin, Path = document.Path, Version = document.Version, Part = i, TotalParts = chunks.Length, Data = chunks[i] }, false, ct).ConfigureAwait(false);
                    }
                    Progress.Advance(++submitted, document.Plugin + "/" + document.Path);
                }
            }
            int epoch = await RequestAsync<int>(HttpMethod.Post, $"backups/{id}/verify", null, false, ct).ConfigureAwait(false);
            var unique = capture.Files.DistinctBy(f => f.Hash).ToArray();
            Progress.Phase(SnapshotPhase.Checking, unique.Length);
            var missing = new List<BackupFileDto>(); int checkedObjects = 0;
            foreach (var batch in unique.Chunk(256))
            {
                await YieldAsync(ct).ConfigureAwait(false);
                await RequestAsync<object>(HttpMethod.Post, $"backups/{id}/lease?epoch={epoch}", null, false, ct).ConfigureAwait(false);
                var objects = await RequestAsync<List<BackupObjectDto>>(HttpMethod.Post, $"files/backups/{id}/objects?epoch={epoch}", batch.Select(f => f.Hash).ToArray(), true, ct).ConfigureAwait(false);
                foreach (var file in batch)
                {
                    var obj = objects.Single(o => o.Hash == file.Hash);
                    if (obj.Available) Progress.Reuse(file.Size); else missing.Add(file);
                    Progress.Advance(++checkedObjects);
                }
            }
            Progress.PlanUploads(missing.Count, missing.Sum(f => f.Size));
            Progress.Phase(SnapshotPhase.Compressing, missing.Count);
            var queued = missing.ToDictionary(f => f.Hash, StringComparer.Ordinal);
            await _uploads.UploadBackupBatchAsync(missing.Select(f => f.Hash).ToArray(), id,
                async (hash, token) =>
                {
                    await YieldAsync(token).ConfigureAwait(false);
                    await RequestAsync<object>(HttpMethod.Post, $"backups/{id}/lease?epoch={epoch}", null, false, token).ConfigureAwait(false);
                    if (serverKey != Key) throw new InvalidOperationException("Account or server changed during backup.");
                    var file = queued[hash];
                    await capture.StageMissingAsync(file, token).ConfigureAwait(false);
                    var compressed = Path.Combine(capture.Staging, hash + ".scf");
                    await SnapshotStagingAdapter.EncodeAsync(Path.Combine(capture.Staging, hash), compressed,
                        capture.GamePathHints.GetValueOrDefault(hash, []).Concat(capture.Files.Where(f => f.Hash == hash).Select(f => f.Path)), token).ConfigureAwait(false);
                    Progress.PreparedUpload(new FileInfo(compressed).Length, file.Size);
                    File.Delete(Path.Combine(capture.Staging, hash));
                    return new SnapshotUploadStream(File.OpenRead(compressed), WaitForPauseAsync, token);
                },
                async (hash, token) =>
                {
                    File.Delete(Path.Combine(capture.Staging, hash + ".scf"));
                    var file = queued[hash]; Progress.Phase(SnapshotPhase.Verifying, path: file.Mod + "/" + file.Path);
                    var verified = await RequestAsync<List<BackupObjectDto>>(HttpMethod.Post,
                        $"files/backups/{id}/objects?epoch={epoch}", new[] { hash }, true, token).ConfigureAwait(false);
                    if (!verified.Single().Available) throw new IOException($"Uploaded content did not pass physical verification: {file.Mod}/{file.Path} ({hash}, {file.Size} raw bytes).");
                    Progress.Uploaded(); File.Delete(Path.Combine(capture.Staging, hash));
                }, (hash, size) =>
                {
                    if (serverKey != Key) throw new InvalidOperationException("Account or server changed during backup.");
                    var file = queued[hash]; string name = file.Mod + "/" + file.Path;
                    Status = "Uploading " + name; Progress.StartUpload(name, size);
                }, new UploadReporter(Progress), ct).ConfigureAwait(false);
            await YieldAsync(ct).ConfigureAwait(false);
            Progress.Phase(SnapshotPhase.Finalizing);
            await RequestAsync<object>(HttpMethod.Post, $"files/backups/{id}/complete?epoch={epoch}", null, true, ct).ConfigureAwait(false);
            await RequestAsync<BackupSummaryDto>(HttpMethod.Post, $"backups/{id}/{(renew ? "renew" : "complete")}?epoch={epoch}", null, false, ct).ConfigureAwait(false);
            state.Success(id, DateTimeOffset.UtcNow); state.Mods = mods; state.Plugins = plugins; state.AllInstalledMods = true; if (!automatic) state.Enabled = automaticAfterSuccess; SaveState();
            _repair.RecordSources(serverKey, capture.Files.Where(f => !f.Mod.StartsWith('@')).Select(f => new Snowcloak.Core.FileRepair.RepairSource(f.Hash, f.Size, root, f.Mod + "/" + f.Path)));
            Progress.Phase(SnapshotPhase.Complete);
            Status = renew ? "Backup verified." : "Backup complete.";
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Progress.Phase(SnapshotPhase.Cancelled); Status = "Backup cancelled. Your previous complete snapshot is preserved."; }
        catch (Exception ex) { Error = ex.Message; Progress.Phase(SnapshotPhase.Failed); state.Failure(DateTimeOffset.UtcNow); SaveState(); Status = "Backup needs attention: " + ex.Message;
            if (automatic && state.Failures == 3) _mediator.Publish(new NotificationMessage("Environment backup needs attention", "Daily backup verification has failed repeatedly. Open /snow backups to inspect it.", NotificationType.Warning)); }
        finally { _activeServerKey = null; _operation = null; _paused = false; _automaticOperation = false; _serial.Release(); }
    }
    private async Task<HttpRequestMessage> MakeRequestAsync(HttpMethod method, string path, bool files, CancellationToken ct)
    {
        if (_activeServerKey != null && _activeServerKey != Key) throw new InvalidOperationException("Account or server changed during backup.");
        var root = files ? _transfers.FilesCdnUri ?? throw new InvalidOperationException("Files service unavailable.") : new Uri(_servers.CurrentRealApiUrl);
        var request = new HttpRequestMessage(method, new Uri(root, "/" + path));
        var token = files ? await _tokens.GetFilesToken(ct).ConfigureAwait(false) : await _tokens.GetOrUpdateToken(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token)) { request.Dispose(); throw new UnauthorizedAccessException("Authentication unavailable."); }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return request;
    }
    public Task<T> ReadAsync<T>(string path) => RequestAsync<T>(HttpMethod.Get, path, null, false, CancellationToken.None);
    public Task<List<BackupObjectDto>> CheckObjectsAsync(Guid id, string[] hashes, CancellationToken ct) => RequestAsync<List<BackupObjectDto>>(HttpMethod.Post, $"files/backups/{id}/objects", hashes, true, ct);
    public async Task<HttpResponseMessage> DownloadObjectAsync(Guid id, string hash, string fallbackUrl, CancellationToken ct)
    {
        if (!_api.SupportsFileRepair) return await _transfers.SendFileDownloadRequestAsync(new Uri(fallbackUrl), ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var request = await MakeRequestAsync(HttpMethod.Get, $"files/backups/{id}/download/{hash}", true, ct).ConfigureAwait(false);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (attempt >= 5 || (int)response.StatusCode != 429) return response;
            var delay = UploadRateLimitRetry.RetryDelay(response.Headers.RetryAfter, attempt + 1); response.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
    private async Task<T> RequestAsync<T>(HttpMethod method, string path, object? body, bool files, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = await MakeRequestAsync(method, path, files, ct).ConfigureAwait(false);
                if (body != null) request.Content = JsonContent.Create(body);
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (attempt < 5 && (int)response.StatusCode == 429)
                {
                    await Task.Delay(UploadRateLimitRetry.RetryDelay(response.Headers.RetryAfter, attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }
                if (attempt < 2 && (int)response.StatusCode >= 500)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * (attempt + 1));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 30)), ct).ConfigureAwait(false); continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Backup request {method} /{path.Split('?')[0]} failed: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
                if (typeof(T) == typeof(object)) return (T)new object();
                return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false) ?? throw new IOException("Empty backup response.");
            }
            catch (HttpRequestException ex) when (attempt < 2 && (ex.StatusCode == null || (int?)ex.StatusCode == 429 || (int?)ex.StatusCode >= 500))
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!_api.IsConnected) continue;
            var state = Current;
            if (state.Due(DateTimeOffset.UtcNow, _started, _penumbra.APIAvailable)) await RunAsync(state.Plugins, true, true, stoppingToken).ConfigureAwait(false);
        }
    }
    public override void Dispose() { Cancel(); _http.Dispose(); base.Dispose(); }
}
