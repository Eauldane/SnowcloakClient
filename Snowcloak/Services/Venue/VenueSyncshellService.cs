using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.Venue;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Housing;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Snowcloak.Services.Venue;

public sealed class VenueSyncshellService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _configService;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly Dictionary<string, AutoJoinedVenue> _autoJoinedVenues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _pendingRemovalTokens = new(StringComparer.Ordinal);
    private readonly Lock _syncRoot = new();
    private VenueSyncshellPrompt? _activePrompt;

    public VenueSyncshellService(ILogger<VenueSyncshellService> logger, SnowMediator mediator, ApiController apiController,
        SnowcloakConfigService configService, PairManager pairManager, ServerConfigurationManager serverConfigurationManager) : base(logger, mediator)
    {
        _apiController = apiController;
        _configService = configService;
        _pairManager = pairManager;
        _serverConfigurationManager = serverConfigurationManager;

        Mediator.Subscribe<HousingPlotEnteredMessage>(this, msg => _ = HandleHousingPlotEntered(msg.Location));
        Mediator.Subscribe<HousingPlotLeftMessage>(this, msg => HandleHousingPlotLeft(msg.Location));
        Mediator.Subscribe<DisconnectedMessage>(this, _ => ClearState());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ClearState();
        return Task.CompletedTask;
    }

    public async Task<bool> JoinVenueShellAsync(Guid promptId)
    {
        var prompt = _activePrompt;
        if (prompt == null || prompt.PromptId != promptId)
            return false;

        var joinGroupId = prompt.Venue.JoinInfo.Group.GID;
        if (_pairManager.Groups.Keys.Any(g => string.Equals(g.GID, joinGroupId, StringComparison.Ordinal)))
            return true;

        try
        {
            var joined = await _apiController.GroupJoin(prompt.Venue.JoinInfo).ConfigureAwait(false);
            if (joined)
            {
                Logger.LogInformation("Joined venue syncshell {GID} for {Venue}", joinGroupId, prompt.Venue.VenueName);
                DisableAutoJoinedSyncshellChat(joinGroupId);
                lock (_syncRoot)
                {
                    _autoJoinedVenues[joinGroupId] = new(prompt.Venue.JoinInfo.Group, prompt.Location);
                    CancelPendingRemoval(joinGroupId);
                    _activePrompt = null;
                }
            }

            return joined;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to join venue syncshell {GID}", joinGroupId);
            return false;
        }
    }

    private void DisableAutoJoinedSyncshellChat(string joinGroupId)
    {
        if (_serverConfigurationManager.HasShellConfigForGid(joinGroupId))
        {
            return;
        }

        var shellConfig = _serverConfigurationManager.GetShellConfigForGid(joinGroupId);
        shellConfig.Enabled = false;
        _serverConfigurationManager.SaveShellConfigForGid(joinGroupId, shellConfig);
    }

    internal bool IsAutoJoined(GroupData group)
    {
        lock (_syncRoot)
        {
            return _autoJoinedVenues.ContainsKey(group.GID);
        }
    }

    private void ClearState()
    {
        Logger.LogDebug("Clearing venue syncshell state");
        lock (_syncRoot)
        {
            foreach (var token in _pendingRemovalTokens.Values)
            {
                token.Cancel();
            }

            _pendingRemovalTokens.Clear();
            _autoJoinedVenues.Clear();
            _activePrompt = null;
        }
    }

    private void CancelPendingRemoval(string groupId)
    {
        lock (_syncRoot)
        {
            if (_pendingRemovalTokens.TryGetValue(groupId, out var pending))
            {
                pending.Cancel();
                _pendingRemovalTokens.Remove(groupId);
            }
        }
    }

    private void CancelPendingRemovalForLocation(HousingPlotLocation location)
    {
        List<string> groupIds;
        lock (_syncRoot)
        {
            groupIds = _autoJoinedVenues.Where(kvp => IsSamePlot(kvp.Value.Location, location)).Select(kvp => kvp.Key).ToList();
            
        }

        foreach (var groupId in groupIds)
        {
            CancelPendingRemoval(groupId);
        }
    }

    private async Task HandleHousingPlotEntered(HousingPlotLocation location)
    {
        CancelPendingRemovalForLocation(location);
        RemoveStaleAutoJoinedVenues();

        if (!_configService.Current.AutoJoinVenueSyncshells)
        {
            Logger.LogTrace("Venue auto-join disabled; ignoring plot {Plot}", location.FullId);
            return;
        }

        if (!_apiController.IsConnected)
        {
            Logger.LogTrace("Not connected; skipping venue lookup for plot {Plot}", location.FullId);
            return;
        }

        var request = new VenueInfoRequestDto(new VenueLocationDto(location.WorldId, location.TerritoryId, location.DivisionId, location.WardId, location.PlotId, location.RoomId, location.IsApartment));
        VenueInfoResponseDto? response;
        try
        {
            response = await _apiController.VenueGetInfoForPlot(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to fetch venue info for plot {Plot}", location.FullId);
            return;
        }

        if (response?.HasVenue != true || response.Venue == null)
            return;

        var joinGroupId = response.Venue.JoinInfo.Group.GID;
        if (_pairManager.Groups.Keys.Any(g => string.Equals(g.GID, joinGroupId, StringComparison.Ordinal)))
        {
            Logger.LogDebug("Already a member of venue syncshell {GID}; skipping prompt", joinGroupId);
            return;
        }

        lock (_syncRoot)
        {
            if (_autoJoinedVenues.ContainsKey(joinGroupId))
            {
                Logger.LogDebug("Already auto-joined venue syncshell {GID}; skipping prompt", joinGroupId);
                return;
            }

            _activePrompt = new VenueSyncshellPrompt(response.Venue, location);
        }

        Mediator.Publish(new OpenVenueSyncshellPopupMessage(_activePrompt));
    }

    private void HandleHousingPlotLeft(HousingPlotLocation location)
    {
        List<AutoJoinedVenue> venuesToLeave;
        lock (_syncRoot)
        {
            venuesToLeave = _autoJoinedVenues.Values.Where(v => IsSamePlot(v.Location, location)).ToList();
            
        }

        foreach (var venue in venuesToLeave)
        {
            ScheduleAutoLeave(venue);
        }
    }

    private void ScheduleAutoLeave(AutoJoinedVenue venue)
    {
        CancelPendingRemoval(venue.Group.GID);

        var tokenSource = new CancellationTokenSource();
        lock (_syncRoot)
        {
            _pendingRemovalTokens[venue.Group.GID] = tokenSource;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), tokenSource.Token).ConfigureAwait(false);
                if (tokenSource.Token.IsCancellationRequested)
                    return;

                if (!_apiController.IsConnected)
                {
                    Logger.LogWarning("Could not auto-leave venue syncshell {GID} because the client is not connected", venue.Group.GID);
                    return;
                }

                await _apiController.GroupLeave(new GroupDto(venue.Group)).ConfigureAwait(false);
                Logger.LogInformation("Auto-left venue syncshell {GID} after grace period", venue.Group.GID);
                lock (_syncRoot)
                {
                    _autoJoinedVenues.Remove(venue.Group.GID);
                }
            }
            catch (TaskCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to auto-leave venue syncshell {GID}", venue.Group.GID);
            }
            finally
            {
                lock (_syncRoot)
                {
                    _pendingRemovalTokens.Remove(venue.Group.GID);
                }
            }
        }, tokenSource.Token);
    }
    
    private static bool IsSamePlot(HousingPlotLocation left, HousingPlotLocation right)
    {
        return left.WorldId == right.WorldId
               && left.TerritoryId == right.TerritoryId
               && left.DivisionId == right.DivisionId
               && left.WardId == right.WardId
               && left.PlotId == right.PlotId
               && left.IsApartment == right.IsApartment;
    }

    private void RemoveStaleAutoJoinedVenues()
    {
        List<string> staleGroupIds;
        lock (_syncRoot)
        {
            var memberGids = new HashSet<string>(_pairManager.Groups.Keys.Select(g => g.GID), StringComparer.Ordinal);
            staleGroupIds = _autoJoinedVenues.Keys.Where(gid => !memberGids.Contains(gid)).ToList();

            foreach (var staleGroupId in staleGroupIds)
            {
                _autoJoinedVenues.Remove(staleGroupId);
                if (_pendingRemovalTokens.TryGetValue(staleGroupId, out var pending))
                {
                    pending.Cancel();
                    _pendingRemovalTokens.Remove(staleGroupId);
                }
            }
        }
    }


    private readonly record struct AutoJoinedVenue(GroupData Group, HousingPlotLocation Location);
}
