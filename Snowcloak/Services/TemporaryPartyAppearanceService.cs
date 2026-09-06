using ElezenTools.Core.Async;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.TemporaryAppearance;
using Snowcloak.API.Protocol;
using Snowcloak.Configuration;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI;

namespace Snowcloak.Services;

public sealed class TemporaryPartyAppearanceService : MediatorSubscriberBase, IHostedService
{
    private readonly ITemporaryRosterReader _rosterReader;
    private readonly ApiController _api;
    private readonly PairManager _pairs;
    private readonly SnowcloakConfigService _config;
    private readonly IFrameTickHandle _tick;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private readonly object _leaseLock = new();
    private readonly Dictionary<(string Uid, TemporaryAppearanceSourceKind Source), long> _leaseDeadlines = [];
    private readonly Dictionary<TemporaryAppearanceSourceKind, SourceState> _sources = new()
    {
        [TemporaryAppearanceSourceKind.Party] = new(),
        [TemporaryAppearanceSourceKind.Alliance] = new(),
    };
    private TemporaryAppearanceDescriptor? _descriptor;
    private int _networkInFlight;
    private int _snapshotRefreshPending;
    private ulong _viewRevision;
    private ulong _peerConsentSequence;

    public TemporaryPartyAppearanceService(ILogger<TemporaryPartyAppearanceService> logger,
        ITemporaryRosterReader rosterReader, ApiController api, PairManager pairs,
        SnowcloakConfigService config, SnowMediator mediator, IFrameScheduler scheduler)
        : base(logger, mediator)
    {
        _rosterReader = rosterReader;
        _api = api;
        _pairs = pairs;
        _config = config;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _tick = scheduler.Register("TemporaryPartyAppearance", TickInterval.EveryMilliseconds(500),
            TickPriority.Normal, OnFrameworkTick, FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);

        Mediator.Subscribe<ConnectedMessage>(this, message => ResetForConnection(message.Connection.TemporaryAppearance));
        Mediator.Subscribe<DisconnectedMessage>(this, _ => ClearLocalState());
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, _ => SuspendAllSources(TemporaryAppearanceOperation.Reset, "ZoneTemporaryAppearance"));
        Mediator.Subscribe<DalamudLogoutMessage>(this, _ => SuspendAllSources(TemporaryAppearanceOperation.Reset, "LogoutTemporaryAppearance"));
        Mediator.Subscribe<TemporaryAppearanceInvalidatedMessage>(this, _ =>
        {
            Interlocked.Exchange(ref _snapshotRefreshPending, 1);
            TryQueueSnapshotRefresh();
        });
    }

    public int ActivePeerCount => _pairs.TemporaryAppearancePeerCount;
    public bool IsSupported => _api.SupportsTemporaryAppearance;

    public int GetRemainingLeaseMs(string uid, TemporaryAppearanceSourceKind source)
    {
        lock (_leaseLock)
        {
            return _leaseDeadlines.TryGetValue((uid, source), out var deadline)
                ? (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue)
                : 0;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _tick.Dispose();
        Mediator.UnsubscribeAll(this);
        await StopAllAsync().ConfigureAwait(false);
        await _backgroundTasks.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        SuspendLocalSources();
        var descriptor = _descriptor;
        if (descriptor == null || !_api.IsConnected)
        {
            ClearLocalState();
            return;
        }
        await _networkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var source in descriptor.Sources.Where(source => _sources.ContainsKey(source.Kind)))
                await SendOperationAsync(descriptor, source.Kind, TemporaryAppearanceOperation.Disable, null).ConfigureAwait(false);
        }
        finally
        {
            _networkGate.Release();
        }
    }

    public async Task StopPeerAsync(string uid)
    {
        var descriptor = _descriptor;
        if (descriptor == null || !_api.IsConnected || string.IsNullOrWhiteSpace(uid))
            return;
        await _networkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = await _api.TemporaryAppearanceSetPeerConsent(new TemporaryAppearancePeerConsentCommand
            {
                ServerEpoch = descriptor.ServerEpoch,
                BindingId = descriptor.BindingId,
                PeerUid = uid,
                PeerCommandSequence = ++_peerConsentSequence,
                AllowTemporarySync = false,
            }).ConfigureAwait(false);
            ApplySnapshot(snapshot);
        }
        finally
        {
            _networkGate.Release();
        }
    }

    private void ResetForConnection(TemporaryAppearanceDescriptor? descriptor)
    {
        _descriptor = descriptor;
        _viewRevision = 0;
        _peerConsentSequence = 0;
        Interlocked.Exchange(ref _snapshotRefreshPending, 0);
        foreach (var state in _sources.Values) state.Reset();
        _pairs.ReconcileTemporaryAppearance(new TemporaryAppearanceSnapshot());
    }

    private void ClearLocalState()
    {
        _descriptor = null;
        Interlocked.Exchange(ref _snapshotRefreshPending, 0);
        foreach (var state in _sources.Values) state.Reset();
        lock (_leaseLock) _leaseDeadlines.Clear();
        _pairs.ReconcileTemporaryAppearance(new TemporaryAppearanceSnapshot());
    }

    private void OnFrameworkTick()
    {
        try
        {
            ExpireLocalLeases();
            var descriptor = _descriptor;
            if (!_config.Current.EnableTemporaryPartyAllianceAppearance || descriptor == null
                || !_api.SupportsTemporaryAppearance)
            {
                if (_sources.Values.Any(state => state.ClaimEnabled))
                    SuspendAllSources(TemporaryAppearanceOperation.Disable, "DisableTemporaryAppearance");
                return;
            }
            if (!_rosterReader.TryCapture(out var capture))
            {
                SuspendAllSources(TemporaryAppearanceOperation.Reset, "ResetTemporaryAppearance");
                return;
            }

            var state = _sources[capture.Source];
            var rosterKey = $"{(byte)capture.Layout}:" + string.Join(',', capture.ContentIds);
            if (!string.Equals(state.CandidateRoster, rosterKey, StringComparison.Ordinal))
            {
                state.CandidateRoster = rosterKey;
                state.StableSamples = 1;
                return;
            }
            state.StableSamples++;
            if (state.StableSamples < 2) return;
            if (!string.Equals(state.AcceptedRoster, rosterKey, StringComparison.Ordinal)
                || DateTimeOffset.UtcNow >= state.RefreshAtUtc)
                QueueNetworkOperation(() => PublishCaptureAsync(descriptor, capture), "PublishTemporaryAppearance");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to capture the temporary party/alliance roster");
        }
    }

    private async Task PublishCaptureAsync(TemporaryAppearanceDescriptor descriptor, TemporaryRosterCapture capture)
    {
        foreach (var other in _sources.Where(entry => entry.Key != capture.Source && entry.Value.ClaimEnabled))
            await SendOperationAsync(descriptor, other.Key, TemporaryAppearanceOperation.Reset, null).ConfigureAwait(false);
        await SendOperationAsync(descriptor, capture.Source, TemporaryAppearanceOperation.ReplaceComplete, capture)
            .ConfigureAwait(false);
    }

    private async Task SendOperationAsync(TemporaryAppearanceDescriptor descriptor,
        TemporaryAppearanceSourceKind sourceKind, TemporaryAppearanceOperation operation, TemporaryRosterCapture? capture)
    {
        if (!_api.IsConnected) return;
        var source = descriptor.Sources.SingleOrDefault(item => item.Kind == sourceKind);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var epoch = source?.ClaimEpochs.SingleOrDefault(item => item.ValidFromUnixMs <= now && item.ValidUntilUnixMs > now);
        if (source == null || epoch == null) return;

        var state = _sources[sourceKind];
        List<CompleteRosterObservation> observations = [];
        var publishedRoster = string.Empty;
        if (operation == TemporaryAppearanceOperation.ReplaceComplete)
        {
            var value = capture ?? throw new InvalidOperationException("A replace command requires a roster capture.");
            if (value.ContentIds.Length > source.MaxMembers) return;
            publishedRoster = $"{(byte)value.Layout}:" + string.Join(',', value.ContentIds);
            var tokens = value.ContentIds.Select(contentId => ComputeToken(epoch, sourceKind,
                    CharacterIdentityProtocol.FromContentId(contentId)))
                .OrderBy(Convert.ToHexString, StringComparer.Ordinal).ToList();
            observations.Add(new CompleteRosterObservation
            {
                ClaimEpochId = epoch.EpochId,
                Layout = value.Layout,
                DeclaredMemberCount = tokens.Count,
                MemberTokens = tokens,
                RosterDigest = TemporaryAppearanceClaimProtocol.ComputeRosterDigest(epoch.ServerScope, epoch.EpochId,
                    sourceKind, value.Layout, tokens),
            });
            if (!string.Equals(state.AcceptedRoster, state.CandidateRoster, StringComparison.Ordinal)) state.Generation++;
        }

        var result = await _api.TemporaryAppearanceUpdate(new TemporaryAppearanceSourceCommand
        {
            ServerEpoch = descriptor.ServerEpoch,
            BindingId = descriptor.BindingId,
            Source = sourceKind,
            SourceGeneration = state.Generation,
            CommandSequence = ++state.Sequence,
            Operation = operation,
            EpochObservations = observations,
            SendCategories = _config.Current.TemporaryPartySendCategories & source.AllowedCategories,
            ReceiveCategories = _config.Current.TemporaryPartyReceiveCategories & source.AllowedCategories,
        }).ConfigureAwait(false);

        state.Sequence = Math.Max(state.Sequence, result.AcceptedCommandSequence);
        if (result.Status is TemporaryAppearanceCommandStatus.Applied or TemporaryAppearanceCommandStatus.Duplicate)
        {
            state.ClaimEnabled = operation == TemporaryAppearanceOperation.ReplaceComplete;
            state.LocalRosterAvailable = state.ClaimEnabled;
            // The framework-thread candidate may have changed while this request was in flight.
            // Record only the roster actually acknowledged by the server so the next tick retries.
            state.AcceptedRoster = state.ClaimEnabled ? publishedRoster : string.Empty;
            if (state.ClaimEnabled)
                state.RefreshAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1000, source.RefreshMs));
        }
        else if (result.Status == TemporaryAppearanceCommandStatus.Disabled)
        {
            state.ClaimEnabled = false;
            state.LocalRosterAvailable = false;
            state.AcceptedRoster = string.Empty;
        }
        ApplySnapshot(result.Snapshot);
    }

    private async Task RefreshSnapshotAsync()
    {
        var descriptor = _descriptor;
        if (descriptor == null || !_api.SupportsTemporaryAppearance) return;
        var snapshot = await _api.TemporaryAppearanceGetSnapshot(new TemporaryAppearanceSnapshotRequest
        {
            ServerEpoch = descriptor.ServerEpoch,
            BindingId = descriptor.BindingId,
            KnownViewRevision = _viewRevision,
        }).ConfigureAwait(false);
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(TemporaryAppearanceSnapshot snapshot)
    {
        var descriptor = _descriptor;
        if (descriptor == null || !snapshot.BindingId.AsSpan().SequenceEqual(descriptor.BindingId)
            || !snapshot.ServerEpoch.AsSpan().SequenceEqual(descriptor.ServerEpoch)
            || snapshot.ViewRevision < _viewRevision) return;
        _viewRevision = snapshot.ViewRevision;
        var allowedSources = _sources.Where(entry => entry.Value.LocalRosterAvailable).Select(entry => entry.Key).ToHashSet();
        var localSnapshot = new TemporaryAppearanceSnapshot
        {
            ServerEpoch = snapshot.ServerEpoch,
            BindingId = snapshot.BindingId,
            ViewRevision = snapshot.ViewRevision,
            ServerNowUnixMs = snapshot.ServerNowUnixMs,
            OwnSources = snapshot.OwnSources,
            Peers = snapshot.Peers.Select(peer => new TemporaryAppearancePeer
            {
                User = peer.User,
                CharacterIdent = peer.CharacterIdent,
                Grants = peer.Grants.Where(grant => allowedSources.Contains(grant.Source)).ToList(),
            }).Where(peer => peer.Grants.Count > 0).ToList(),
        };
        var now = Environment.TickCount64;
        lock (_leaseLock)
        {
            _leaseDeadlines.Clear();
            foreach (var peer in localSnapshot.Peers)
            foreach (var grant in peer.Grants)
            {
                var maxTtl = descriptor.Sources.FirstOrDefault(source => source.Kind == grant.Source)?.ClaimTtlMs ?? 0;
                var remaining = Math.Clamp(grant.RemainingMs, 0, Math.Max(0, maxTtl));
                if (remaining > 0) _leaseDeadlines[(peer.User.UID, grant.Source)] = now + remaining;
            }
        }
        _pairs.ReconcileTemporaryAppearance(localSnapshot);
    }

    private void ExpireLocalLeases()
    {
        var now = Environment.TickCount64;
        lock (_leaseLock)
        {
            if (!_leaseDeadlines.Values.Any(deadline => deadline <= now)) return;
            foreach (var key in _leaseDeadlines.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
                _leaseDeadlines.Remove(key);
        }

        var peers = _pairs.GetTemporaryAppearancePairs().Select(pair => new TemporaryAppearancePeer
        {
            User = pair.UserData,
            CharacterIdent = pair.Ident,
            Grants = pair.TemporaryAppearance!.Grants.Where(grant => GetRemainingLeaseMs(pair.UserData.UID, grant.Source) > 0)
                .ToList(),
        }).Where(peer => peer.Grants.Count > 0).ToList();
        _pairs.ReconcileTemporaryAppearance(new TemporaryAppearanceSnapshot { Peers = peers });
        Interlocked.Exchange(ref _snapshotRefreshPending, 1);
        TryQueueSnapshotRefresh();
    }

    private void SuspendAllSources(TemporaryAppearanceOperation operation, string operationName)
    {
        var active = _sources.Where(entry => entry.Value.ClaimEnabled).Select(entry => entry.Key).ToArray();
        SuspendLocalSources();
        var descriptor = _descriptor;
        if (descriptor == null || !_api.IsConnected || active.Length == 0) return;
        QueueNetworkOperation(async () =>
        {
            foreach (var sourceKind in active)
                await SendOperationAsync(descriptor, sourceKind, operation, null).ConfigureAwait(false);
        }, operationName);
    }

    private void SuspendLocalSources()
    {
        foreach (var state in _sources.Values)
        {
            state.CandidateRoster = string.Empty;
            state.AcceptedRoster = string.Empty;
            state.StableSamples = 0;
            state.LocalRosterAvailable = false;
        }
        lock (_leaseLock) _leaseDeadlines.Clear();
        _pairs.ReconcileTemporaryAppearance(new TemporaryAppearanceSnapshot());
    }

    private void TryQueueSnapshotRefresh()
    {
        if (Volatile.Read(ref _snapshotRefreshPending) == 0) return;
        if (QueueNetworkOperation(RefreshSnapshotAsync, nameof(RefreshSnapshotAsync)))
            Interlocked.Exchange(ref _snapshotRefreshPending, 0);
    }

    private bool QueueNetworkOperation(Func<Task> operation, string name)
    {
        if (Interlocked.CompareExchange(ref _networkInFlight, 1, 0) != 0) return false;
        _ = _backgroundTasks.Run(async () =>
        {
            try
            {
                await _networkGate.WaitAsync().ConfigureAwait(false);
                try { await operation().ConfigureAwait(false); }
                finally { _networkGate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.LogWarning(ex, "Temporary appearance operation {Operation} failed", name); }
            finally
            {
                Interlocked.Exchange(ref _networkInFlight, 0);
                TryQueueSnapshotRefresh();
            }
        }, name);
        return true;
    }

    private static byte[] ComputeToken(TemporaryAppearanceClaimEpoch epoch,
        TemporaryAppearanceSourceKind source, string ident)
        => TemporaryAppearanceClaimProtocol.ComputeMemberToken(epoch.KeyMaterial, epoch.ServerScope,
            epoch.EpochId, source, ident);

    private sealed class SourceState
    {
        public string CandidateRoster { get; set; } = string.Empty;
        public string AcceptedRoster { get; set; } = string.Empty;
        public int StableSamples { get; set; }
        public bool ClaimEnabled { get; set; }
        public bool LocalRosterAvailable { get; set; }
        public ulong Generation { get; set; }
        public ulong Sequence { get; set; }
        public DateTimeOffset RefreshAtUtc { get; set; } = DateTimeOffset.MinValue;

        public void Reset()
        {
            CandidateRoster = string.Empty;
            AcceptedRoster = string.Empty;
            StableSamples = 0;
            ClaimEnabled = false;
            LocalRosterAvailable = false;
            Generation = 0;
            Sequence = 0;
            RefreshAtUtc = DateTimeOffset.MinValue;
        }
    }
}
