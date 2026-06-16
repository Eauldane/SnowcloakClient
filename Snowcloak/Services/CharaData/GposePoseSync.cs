using Dalamud.Game.ClientState.Objects.SubKinds;
using ElezenTools.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.Core.CharaData;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Diagnostics;

namespace Snowcloak.Services.CharaData;

internal sealed class GposePoseSync : IDisposable
{
    private readonly ILogger _logger;
    private readonly GposeLobbySession _session;
    private readonly IpcCallerBrio _brio;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ApiController _apiController;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly CancellationTokenSource _loopCts = new();
    private volatile bool _forceResend;
    private int _disposed;

    public GposePoseSync(ILogger logger, GposeLobbySession session, IpcCallerBrio brio,
        DalamudUtilService dalamudUtil, ApiController apiController)
    {
        _logger = logger;
        _session = session;
        _brio = brio;
        _dalamudUtil = dalamudUtil;
        _apiController = apiController;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _ = _backgroundTasks.Run(() => CaptureLoop(_loopCts.Token), nameof(GposePoseSync));
    }

    public void ForceResend() => _forceResend = true;

    public void OnReceivePose(UserData userData, PoseData poseData)
    {
        if (_session.TryGetUser(userData.UID, out var member))
            member.FullPoseData = poseData;
    }

    public async Task ApplyPose(GposeLobbyUserData member)
    {
        if (member.ApplicablePoseData == null || member.Address == nint.Zero)
            return;

        await _brio.SetPoseAsync(member.Address, PoseDataCodec.ToBrioJson(member.ApplicablePoseData.Value)).ConfigureAwait(false);
    }

    private async Task CaptureLoop(CancellationToken ct)
    {
        var sinceChange = Stopwatch.StartNew();
        PoseData? lastSentPose = null;

        while (!ct.IsCancellationRequested)
        {
            var delay = sinceChange.Elapsed < GposeCadence.PoseActiveWindow ? GposeCadence.PoseActiveTick : GposeCadence.PoseIdleTick;
            await Task.Delay(delay, ct).ConfigureAwait(false);

            if (!_dalamudUtil.IsInGpose) continue;
            if (!_session.IsInLobby || !_session.HasMembers) continue;

            try
            {
                IPlayerCharacter? chara = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
                if (chara == null) continue;

                var gposeChara = (IPlayerCharacter?)await _dalamudUtil
                    .GetGposeCharacterFromObjectTableByNameAsync(chara.Name.TextValue, true).ConfigureAwait(false);
                if (gposeChara == null || gposeChara.Address == nint.Zero) continue;

                var poseJson = await _brio.GetPoseAsync(gposeChara.Address).ConfigureAwait(false);
                if (string.IsNullOrEmpty(poseJson)) continue;

                var poseData = PoseDataCodec.FromBrioJson(poseJson);
                if (poseData.Bones.Count == 0 && poseData.MainHand.Count == 0 && poseData.OffHand.Count == 0)
                    continue;

                var changed = lastSentPose == null || !PoseEquals(poseData, lastSentPose.Value);
                if (changed)
                    sinceChange.Restart();

                if (!changed && !_forceResend)
                    continue;

                _forceResend = false;
                await _apiController.GposeLobbyPushPoseData(poseData).ConfigureAwait(false);
                lastSentPose = poseData;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during GPose pose capture");
            }
        }
    }

    private static bool PoseEquals(PoseData a, PoseData b)
    {
        return BonesEqual(a.Bones, b.Bones) && BonesEqual(a.MainHand, b.MainHand) && BonesEqual(a.OffHand, b.OffHand);
    }

    private static bool BonesEqual(Dictionary<string, BoneData>? x, Dictionary<string, BoneData>? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        if (x.Count != y.Count) return false;

        foreach (var (key, value) in x)
        {
            if (!y.TryGetValue(key, out var other) || !other.Equals(value))
                return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _loopCts.Cancel();
        _backgroundTasks.StopAccepting();
        _backgroundTasks.StopSynchronously(_logger, TimeSpan.FromSeconds(2), nameof(GposePoseSync));
        _loopCts.Dispose();
    }
}
