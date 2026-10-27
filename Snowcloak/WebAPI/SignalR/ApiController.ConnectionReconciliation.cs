using Snowcloak.API.Dto.User;
using Snowcloak.Services.Mediator;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    private async Task ReconcileConnectionState(bool resumed)
    {
        _chatPairStateReady = false;
        var timer = Stopwatch.StartNew();
        long listMilliseconds;
        long memberMilliseconds = 0;
        var onlineTask = UserGetOnlinePairs();
        if (resumed)
        {
            var online = await onlineTask.ConfigureAwait(false);
            listMilliseconds = timer.ElapsedMilliseconds;
            timer.Restart();
            _pairManager.ReconcileOnlineState(online);
        }
        else
        {
            var userPairsTask = UserGetPairedClients();
            var groupsTask = GroupsGetAll();
            await Task.WhenAll(onlineTask, userPairsTask, groupsTask).ConfigureAwait(false);
            var groups = await groupsTask.ConfigureAwait(false);
            listMilliseconds = timer.ElapsedMilliseconds;
            timer.Restart();
            var groupUsers = await Task.WhenAll(groups.Select(GroupsGetUsersInGroup)).ConfigureAwait(false);
            memberMilliseconds = timer.ElapsedMilliseconds;
            timer.Restart();
            _pairManager.ReconcileServerState(await userPairsTask.ConfigureAwait(false), groups,
                groupUsers.SelectMany(users => users).ToList(), await onlineTask.ConfigureAwait(false));
            _sessionResumeState.CommitFullResync();
        }

        var pairMilliseconds = timer.ElapsedMilliseconds;
        timer.Restart();
        _chatPairStateReady = true;
        Mediator.Publish(new ChatPairStateReadyMessage());
        Logger.LogInformation("Chat bootstrap ready: lists={ListMs}ms, group members={MemberMs}ms, local pairs={PairMs}ms, ready dispatch={DispatchMs}ms",
            listMilliseconds, memberMilliseconds, pairMilliseconds, timer.ElapsedMilliseconds);
        _sessionGraceFlight.Cancel();
        
        var visible = _pairManager.GetVisibleUsers()
            .Select(user => new OnlineUserIdentDto(user, string.Empty))
            .ToList();
        _ = _backgroundTasks.Run(() => ResolveManifestsForVisiblePairs(visible), nameof(ResolveManifestsForVisiblePairs));
    }
}
