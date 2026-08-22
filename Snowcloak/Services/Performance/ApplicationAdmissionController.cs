using Snowcloak.API.Data.Extensions;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.PlayerData.Pairs;

namespace Snowcloak.Services.Performance;

public sealed class ApplicationAdmissionController : IDisposable
{
    private const int MaxApplicationsPerFrame = 2;
    private const int MaxRedrawApplicationsPerFrame = 1;

    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IFrameTickHandle _tick;
    private readonly Lock _sync = new();
    private readonly List<ApplicationAdmissionRequest> _queue = [];
    private long _nextSequence;
    private bool _disposed;

    public ApplicationAdmissionController(DalamudUtilService dalamudUtilService, IFrameScheduler frameScheduler)
    {
        ArgumentNullException.ThrowIfNull(dalamudUtilService);
        ArgumentNullException.ThrowIfNull(frameScheduler);

        _dalamudUtilService = dalamudUtilService;
        _tick = frameScheduler.Register("PairApplicationAdmission", TickInterval.EveryFrame, TickPriority.High, ReleaseQueuedApplications,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
    }

    public Task WaitForSlotAsync(Pair pair, bool requiresRedraw, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(pair);

        if (token.IsCancellationRequested)
        {
            return Task.FromCanceled(token);
        }

        var request = new ApplicationAdmissionRequest(pair, requiresRedraw, Interlocked.Increment(ref _nextSequence));
        request.AttachCancellation(token);

        lock (_sync)
        {
            if (_disposed)
            {
                request.Cancel();
                return request.Task;
            }

            _queue.Add(request);
        }

        return request.Task;
    }

    public void Dispose()
    {
        List<ApplicationAdmissionRequest> requests;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            requests = _queue.ToList();
            _queue.Clear();
        }

        foreach (var request in requests)
        {
            request.Cancel();
        }

        _tick.Dispose();
    }

    private void ReleaseQueuedApplications()
    {
        HashSet<uint> partyMemberIds = _dalamudUtilService.GetPartyPlayerCharacters()
            .Select(member => member.EntityId)
            .Where(id => id != uint.MaxValue)
            .ToHashSet();

        List<ApplicationAdmissionRequest> released = [];
        lock (_sync)
        {
            _queue.RemoveAll(request => request.IsCompleted);
            if (_queue.Count == 0)
            {
                return;
            }

            _queue.Sort((left, right) => Compare(left, right, partyMemberIds));

            var applicationCount = 0;
            var redrawCount = 0;
            foreach (var request in _queue)
            {
                if (applicationCount >= MaxApplicationsPerFrame)
                {
                    break;
                }

                if (request.RequiresRedraw && redrawCount >= MaxRedrawApplicationsPerFrame)
                {
                    continue;
                }

                released.Add(request);
                applicationCount++;
                if (request.RequiresRedraw)
                {
                    redrawCount++;
                }
            }

            foreach (var request in released)
            {
                _queue.Remove(request);
            }
        }

        foreach (var request in released)
        {
            request.Release();
        }
    }

    private static int Compare(ApplicationAdmissionRequest left, ApplicationAdmissionRequest right, HashSet<uint> partyMemberIds)
    {
        var byPriority = GetPriority(left.Pair, partyMemberIds).CompareTo(GetPriority(right.Pair, partyMemberIds));
        if (byPriority != 0)
        {
            return byPriority;
        }

        return left.Sequence.CompareTo(right.Sequence);
    }

    private static int GetPriority(Pair pair, HashSet<uint> partyMemberIds)
    {
        if (pair.UserPair != null)
        {
            return 0;
        }

        if (partyMemberIds.Contains(pair.PlayerCharacterId))
        {
            return 1;
        }

        if (pair.GroupPair.Keys.Any(group => string.Equals(group.OwnerUID, pair.UserData.UID, StringComparison.Ordinal)))
        {
            return 2;
        }

        if (pair.GroupPair.Values.Any(groupPair => groupPair.GroupPairStatusInfo.IsModerator()))
        {
            return 3;
        }

        if (pair.GroupPair.Values.Any(groupPair => groupPair.GroupPairStatusInfo.IsPinned()))
        {
            return 4;
        }

        return 5;
    }

    private sealed class ApplicationAdmissionRequest
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _cancellation;
        private int _completed;

        public ApplicationAdmissionRequest(Pair pair, bool requiresRedraw, long sequence)
        {
            Pair = pair;
            RequiresRedraw = requiresRedraw;
            Sequence = sequence;
        }

        public Pair Pair { get; }
        public bool RequiresRedraw { get; }
        public long Sequence { get; }
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public Task Task => _completion.Task;

        public void AttachCancellation(CancellationToken token)
        {
            _cancellation = token.Register(static state => ((ApplicationAdmissionRequest)state!).Cancel(), this);
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            _cancellation.Dispose();
            _completion.TrySetResult();
        }

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            _cancellation.Dispose();
            _completion.TrySetCanceled();
        }
    }
}
