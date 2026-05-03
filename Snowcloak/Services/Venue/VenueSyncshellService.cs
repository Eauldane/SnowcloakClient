using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.Venue;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
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
    private static readonly TimeSpan AutoLeaveGracePeriod = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan AutoLeaveWarningPeriod = TimeSpan.FromMinutes(5);
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
        Mediator.Subscribe<ConnectedMessage>(this, _ => RestorePersistedVenueState());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => ClearRuntimeState());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RestorePersistedVenueState();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ClearRuntimeState();
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
                var autoJoinedVenue = new AutoJoinedVenue(prompt.Venue.JoinInfo.Group, prompt.Location);
                CancelPendingRemoval(joinGroupId, clearPersistedDeadline: true);
                lock (_syncRoot)
                {
                    _autoJoinedVenues[joinGroupId] = autoJoinedVenue;
                    _activePrompt = null;
                }

                UpsertPersistedVenue(autoJoinedVenue, leaveAfterUtc: null);
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

    private void ClearRuntimeState()
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

    private void CancelPendingRemoval(string groupId, bool clearPersistedDeadline = false)
    {
        lock (_syncRoot)
        {
            if (_pendingRemovalTokens.TryGetValue(groupId, out var pending))
            {
                pending.Cancel();
                _pendingRemovalTokens.Remove(groupId);
            }
        }

        if (clearPersistedDeadline)
        {
            ClearPersistedLeaveDeadline(groupId);
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
            CancelPendingRemoval(groupId, clearPersistedDeadline: true);
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
        ScheduleAutoLeave(venue, DateTime.UtcNow.Add(AutoLeaveGracePeriod), persistDeadline: true);
    }

    private void ScheduleAutoLeave(AutoJoinedVenue venue, DateTime leaveAfterUtc, bool persistDeadline, bool leaveWarningShown = false)
    {
        CancelPendingRemoval(venue.Group.GID);

        if (persistDeadline)
        {
            UpsertPersistedVenue(venue, leaveAfterUtc);
        }

        var tokenSource = new CancellationTokenSource();
        lock (_syncRoot)
        {
            _pendingRemovalTokens[venue.Group.GID] = tokenSource;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ShowAutoLeaveWarningWhenDue(venue, leaveAfterUtc, leaveWarningShown, tokenSource.Token).ConfigureAwait(false);

                var delay = leaveAfterUtc - DateTime.UtcNow;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                await Task.Delay(delay, tokenSource.Token).ConfigureAwait(false);
                if (tokenSource.Token.IsCancellationRequested)
                    return;

                if (!_apiController.IsConnected)
                {
                    Logger.LogInformation("Deferring auto-leave for venue syncshell {GID} until the client reconnects", venue.Group.GID);
                    return;
                }

                if (!IsGroupMember(venue.Group.GID))
                {
                    RemovePersistedVenue(venue.Group.GID);
                    lock (_syncRoot)
                    {
                        _autoJoinedVenues.Remove(venue.Group.GID);
                    }

                    return;
                }

                await _apiController.GroupLeave(new GroupDto(venue.Group)).ConfigureAwait(false);
                Logger.LogInformation("Auto-left venue syncshell {GID} after grace period", venue.Group.GID);
                RemovePersistedVenue(venue.Group.GID);
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
                    if (_pendingRemovalTokens.TryGetValue(venue.Group.GID, out var pending) && ReferenceEquals(pending, tokenSource))
                    {
                        _pendingRemovalTokens.Remove(venue.Group.GID);
                    }
                }

                tokenSource.Dispose();
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

        foreach (var staleGroupId in staleGroupIds)
        {
            RemovePersistedVenue(staleGroupId);
        }
    }

    private void RestorePersistedVenueState()
    {
        List<AutoJoinedVenue> venuesWithPendingLeave = [];
        var persisted = GetPersistedVenues();

        lock (_syncRoot)
        {
            foreach (var entry in persisted)
            {
                if (string.IsNullOrWhiteSpace(entry.GroupGid))
                {
                    continue;
                }

                var venue = new AutoJoinedVenue(
                    new GroupData(entry.GroupGid, entry.GroupAlias, entry.GroupHexString),
                    new HousingPlotLocation(entry.WorldId, entry.TerritoryId, entry.DivisionId, entry.WardId, entry.PlotId, entry.RoomId, entry.IsApartment));

                _autoJoinedVenues[entry.GroupGid] = venue;
                if (entry.LeaveAfterUtc.HasValue)
                {
                    venuesWithPendingLeave.Add(venue);
                }
            }
        }

        foreach (var venue in venuesWithPendingLeave)
        {
            var entry = persisted.FirstOrDefault(v => string.Equals(v.GroupGid, venue.Group.GID, StringComparison.Ordinal));
            if (entry?.LeaveAfterUtc != null)
            {
                ScheduleAutoLeave(venue, NormalizeUtc(entry.LeaveAfterUtc.Value), persistDeadline: false, entry.LeaveWarningShown);
            }
        }

        if (_apiController.IsConnected)
        {
            RemoveStaleAutoJoinedVenues();
        }
    }

    private List<VenueAutoJoinedSyncshell> GetPersistedVenues()
    {
        return _configService.Current.AutoJoinedVenueSyncshells ??= [];
    }

    private void UpsertPersistedVenue(AutoJoinedVenue venue, DateTime? leaveAfterUtc)
    {
        var persisted = GetPersistedVenues();
        var entry = persisted.SingleOrDefault(v => string.Equals(v.GroupGid, venue.Group.GID, StringComparison.Ordinal));
        if (entry == null)
        {
            entry = new VenueAutoJoinedSyncshell
            {
                GroupGid = venue.Group.GID,
                JoinedAtUtc = DateTime.UtcNow
            };
            persisted.Add(entry);
        }

        entry.GroupAlias = venue.Group.Alias;
        entry.GroupHexString = venue.Group.HexString;
        entry.WorldId = venue.Location.WorldId;
        entry.TerritoryId = venue.Location.TerritoryId;
        entry.DivisionId = venue.Location.DivisionId;
        entry.WardId = venue.Location.WardId;
        entry.PlotId = venue.Location.PlotId;
        entry.RoomId = venue.Location.RoomId;
        entry.IsApartment = venue.Location.IsApartment;
        entry.UpdatedAtUtc = DateTime.UtcNow;
        entry.LeaveAfterUtc = leaveAfterUtc.HasValue ? NormalizeUtc(leaveAfterUtc.Value) : null;
        entry.LeaveWarningShown = false;
        _configService.Save();
    }

    private void ClearPersistedLeaveDeadline(string groupId)
    {
        var entry = GetPersistedVenues().SingleOrDefault(v => string.Equals(v.GroupGid, groupId, StringComparison.Ordinal));
        if (entry?.LeaveAfterUtc == null)
        {
            return;
        }

        entry.LeaveAfterUtc = null;
        entry.LeaveWarningShown = false;
        entry.UpdatedAtUtc = DateTime.UtcNow;
        _configService.Save();
    }

    private async Task ShowAutoLeaveWarningWhenDue(AutoJoinedVenue venue, DateTime leaveAfterUtc, bool warningAlreadyShown, CancellationToken token)
    {
        if (warningAlreadyShown)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (leaveAfterUtc <= now)
        {
            return;
        }

        var warningAtUtc = leaveAfterUtc - AutoLeaveWarningPeriod;
        var warningDelay = warningAtUtc - now;
        if (warningDelay > TimeSpan.Zero)
        {
            await Task.Delay(warningDelay, token).ConfigureAwait(false);
        }

        if (token.IsCancellationRequested || leaveAfterUtc <= DateTime.UtcNow)
        {
            return;
        }

        PublishAutoLeaveWarning(venue);
        MarkPersistedLeaveWarningShown(venue.Group.GID);
    }

    private void PublishAutoLeaveWarning(AutoJoinedVenue venue)
    {
        Mediator.Publish(new NotificationMessage(
            "Venue syncshell auto-leave",
            $"You will leave {venue.Group.AliasOrGID} in 5 minutes unless you return to the venue.",
            NotificationType.Warning,
            TimeSpan.FromSeconds(10)));

        Logger.LogInformation("Venue syncshell {GID} will auto-leave in {Minutes} minutes", venue.Group.GID, AutoLeaveWarningPeriod.TotalMinutes);
    }

    private void MarkPersistedLeaveWarningShown(string groupId)
    {
        var entry = GetPersistedVenues().SingleOrDefault(v => string.Equals(v.GroupGid, groupId, StringComparison.Ordinal));
        if (entry == null || entry.LeaveWarningShown)
        {
            return;
        }

        entry.LeaveWarningShown = true;
        entry.UpdatedAtUtc = DateTime.UtcNow;
        _configService.Save();
    }

    private void RemovePersistedVenue(string groupId)
    {
        var persisted = GetPersistedVenues();
        if (persisted.RemoveAll(v => string.Equals(v.GroupGid, groupId, StringComparison.Ordinal)) > 0)
        {
            _configService.Save();
        }
    }

    private bool IsGroupMember(string groupId)
    {
        return _pairManager.Groups.Keys.Any(g => string.Equals(g.GID, groupId, StringComparison.Ordinal));
    }

    private static DateTime NormalizeUtc(DateTime timestamp)
    {
        return timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };
    }


    private readonly record struct AutoJoinedVenue(GroupData Group, HousingPlotLocation Location);
}
