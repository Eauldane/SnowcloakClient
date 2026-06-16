using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.User;
using Snowcloak.Core.Pairing;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI;

namespace Snowcloak.Services.Pairing;

internal sealed class PairRequestInbox
{
    private readonly ILogger _logger;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly Lazy<ApiController> _apiController;
    private readonly SnowMediator _mediator;
    private readonly IToastGui _toastGui;
    private readonly IChatGui _chatGui;
    private readonly Func<string, bool, Task<PairRequestFilterResult>> _autoReject;
    private readonly Func<PairingRequestDto, bool, RequesterDisplay> _displayResolver;
    private readonly Func<string, bool> _cacheRequesterSnapshot;
    private readonly Action<PairingRequestDto, string> _applyAutoNote;
    private readonly ConcurrentDictionary<Guid, PendingPairRequest> _pendingRequests = new();

    public PairRequestInbox(ILogger logger, BackgroundTaskTracker backgroundTasks,
        Lazy<ApiController> apiController, SnowMediator mediator, IToastGui toastGui, IChatGui chatGui,
        Func<string, bool, Task<PairRequestFilterResult>> autoReject,
        Func<PairingRequestDto, bool, RequesterDisplay> displayResolver,
        Func<string, bool> cacheRequesterSnapshot,
        Action<PairingRequestDto, string> applyAutoNote)
    {
        _logger = logger;
        _backgroundTasks = backgroundTasks;
        _apiController = apiController;
        _mediator = mediator;
        _toastGui = toastGui;
        _chatGui = chatGui;
        _autoReject = autoReject;
        _displayResolver = displayResolver;
        _cacheRequesterSnapshot = cacheRequesterSnapshot;
        _applyAutoNote = applyAutoNote;
    }

    public IReadOnlyCollection<PairingRequestDto> GetPendingRequests()
        => _pendingRequests.Values.Select(p => p.Request).ToList();

    public void Receive(PairingRequestDto dto)
    {
        _ = _backgroundTasks.Run(() => HandleRequestAsync(dto), nameof(HandleRequestAsync));
    }

    public void Clear()
    {
        if (_pendingRequests.IsEmpty)
            return;

        _pendingRequests.Clear();
        _mediator.Publish(new PairingRequestListChangedMessage());
    }

    public async Task RespondAsync(PairingRequestDto request, bool accepted, string? reason = null)
    {
        var note = GetDisplayName(request);
        await RespondWithDecisionAsync(request.RequestId, accepted, reason).ConfigureAwait(false);

        if (accepted)
            _applyAutoNote(request, note);

        _pendingRequests.TryRemove(request.RequestId, out _);
        _mediator.Publish(new PairingRequestListChangedMessage());
    }

    public Task RespondAsync(Guid requestId, bool accepted, string? reason = null)
    {
        if (_pendingRequests.TryGetValue(requestId, out var request))
            return RespondAsync(request.Request, accepted, reason);

        return RespondWithDecisionAsync(requestId, accepted, reason);
    }

    public async Task DeclineAllPendingRequestsAsync(string? reason = null)
    {
        var pending = _pendingRequests.Values.Select(p => p.Request).ToList();
        foreach (var request in pending)
        {
            await RespondAsync(request, false, reason).ConfigureAwait(false);
        }
    }

    public async Task EvaluateAsync(HashSet<string> nearbySet)
    {
        foreach (var pending in _pendingRequests.Values)
        {
            var request = pending.Request;
            if (!nearbySet.Contains(request.RequesterIdent))
                continue;

            var result = await _autoReject(request.RequesterIdent, false).ConfigureAwait(false);
            if (!result.ShouldReject)
                continue;

            if (pending.DeferredAutoFilter)
            {
                await RespondAsync(request, false, reason: null).ConfigureAwait(false);
                continue;
            }

            await RespondAsync(request, false, result.Reason).ConfigureAwait(false);
            var requesterName = GetDisplayName(request);
            var message = $"{requesterName}'s pending pairing request was auto-rejected after they came into range and were found to match your filters.";

            _toastGui.ShowNormal(message);
        }
    }

    private async Task HandleRequestAsync(PairingRequestDto dto)
    {
        if (IsMalformed(dto))
        {
            _logger.LogWarning("Rejecting malformed pair request: missing requester ident and UID (RequestId: {RequestId})", dto.RequestId);
            await RespondAsync(dto.RequestId, false, "Malformed pairing request. Try moving a little closer?").ConfigureAwait(false);
            return;
        }

        _cacheRequesterSnapshot(dto.RequesterIdent);
        var result = await _autoReject(dto.RequesterIdent, true).ConfigureAwait(false);
        if (result.ShouldReject)
        {
            await RespondAsync(dto.RequestId, false, result.Reason).ConfigureAwait(false);
            return;
        }

        _pendingRequests[dto.RequestId] = new PendingPairRequest(dto, result.WasDeferred);
        _mediator.Publish(new PairingRequestReceivedMessage(dto));
        _mediator.Publish(new PairingRequestListChangedMessage());
        var requesterName = GetDisplayName(dto, setNoteFromNearby: true);
        _toastGui.ShowQuest(requesterName + " sent a pairing request.");
        _chatGui.Print($"[Snowcloak] {requesterName} sent a pairing request.");
    }

    private async Task RespondWithDecisionAsync(Guid requestId, bool accepted, string? reason)
    {
        try
        {
            await _apiController.Value
                .UserRespondToPairRequest(new PairingRequestDecisionDto(requestId, accepted, reason))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to respond to request {RequestId}", requestId);
        }
    }

    private string GetDisplayName(PairingRequestDto dto, bool setNoteFromNearby = false)
        => _displayResolver(dto, setNoteFromNearby).NameOrUid;

    private static bool IsMalformed(PairingRequestDto dto)
    {
        var uid = dto.Requester?.UID;
        return string.IsNullOrWhiteSpace(dto.RequesterIdent) && string.IsNullOrWhiteSpace(uid);
    }

    private readonly record struct PendingPairRequest(PairingRequestDto Request, bool DeferredAutoFilter);
}
