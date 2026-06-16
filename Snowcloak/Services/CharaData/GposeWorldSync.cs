using Dalamud.Game.ClientState.Objects.Types;
using ElezenTools.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.Interop;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using System.Numerics;

namespace Snowcloak.Services.CharaData;

internal sealed class GposeWorldSync : IDisposable
{
    private readonly ILogger _logger;
    private readonly GposeLobbySession _session;
    private readonly IpcCallerBrio _brio;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ApiController _apiController;
    private readonly VfxSpawnManager _vfxSpawnManager;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly CancellationTokenSource _loopCts = new();
    private WorldData? _lastWorldData;
    private bool _forceResend;
    private int _disposed;

    public GposeWorldSync(ILogger logger, GposeLobbySession session, IpcCallerBrio brio,
        DalamudUtilService dalamudUtil, ApiController apiController, VfxSpawnManager vfxSpawnManager)
    {
        _logger = logger;
        _session = session;
        _brio = brio;
        _dalamudUtil = dalamudUtil;
        _apiController = apiController;
        _vfxSpawnManager = vfxSpawnManager;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _ = _backgroundTasks.Run(() => CaptureLoop(_loopCts.Token), nameof(GposeWorldSync));
    }

    public WorldData? OwnWorldData => _lastWorldData;

    public void ForceResend() => _forceResend = true;

    public void ResetOwn() => _lastWorldData = null;

    public void OnReceiveWorld(UserData userData, WorldData worldData)
    {
        if (!_session.TryGetUser(userData.UID, out var member))
            return;

        member.WorldData = worldData;
        _ = member.SetWorldDataDescriptor(_dalamudUtil);
    }

    public async Task ApplyWorld(GposeLobbyUserData member)
    {
        if (member.WorldData == null || member.Address == nint.Zero)
            return;

        await _brio.ApplyTransformAsync(member.Address, member.WorldData.Value).ConfigureAwait(false);
    }

    public void UpdateWisps(DateTime frameworkTime)
    {
        foreach (var member in _session.Members)
        {
            if (member.SpawnedVfxId == null || member.UpdateStart == null)
                continue;

            var secondsElapsed = frameworkTime.Subtract(member.UpdateStart.Value).TotalSeconds;
            if (secondsElapsed >= GposeCadence.WispLerpDuration.TotalSeconds)
            {
                member.LastWorldPosition = member.TargetWorldPosition;
                member.TargetWorldPosition = null;
                member.UpdateStart = null;
            }
            else
            {
                var lerp = Vector3.Lerp(member.LastWorldPosition ?? Vector3.One, member.TargetWorldPosition ?? Vector3.One, (float)secondsElapsed);
                _vfxSpawnManager.MoveObject(member.SpawnedVfxId.Value, lerp);
            }
        }
    }

    public void DespawnAllWisps()
    {
        foreach (var member in _session.Members)
        {
            DespawnWisp(member);
        }
    }

    public void DespawnWisp(GposeLobbyUserData member)
    {
        _ = Service.RunOnFrameworkAsync(() => _vfxSpawnManager.DespawnObject(member.SpawnedVfxId));
    }

    private async Task CaptureLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_dalamudUtil.IsInGpose ? GposeCadence.WorldTickGpose : GposeCadence.WorldTickOverworld, ct).ConfigureAwait(false);

            if (!_session.IsInLobby || !_session.HasMembers) continue;

            try
            {
                var worldData = await CaptureOwnWorldData().ConfigureAwait(false);
                if (worldData == null) continue;

                if (_forceResend || worldData != _lastWorldData)
                {
                    _forceResend = false;
                    await _apiController.GposeLobbyPushWorldData(worldData.Value).ConfigureAwait(false);
                    _lastWorldData = worldData;
                    _logger.LogTrace("WorldData (gpose: {gpose}): {data}", _dalamudUtil.IsInGpose, worldData);
                }

                await UpdateMemberWisps(worldData.Value).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during GPose world capture");
            }
        }
    }

    private async Task<WorldData?> CaptureOwnWorldData()
    {
        ICharacter? player = await _dalamudUtil.GetPlayerCharacterAsync().ConfigureAwait(false);
        if (player == null) return null;

        WorldData worldData;
        if (_dalamudUtil.IsInGpose)
        {
            player = await _dalamudUtil.GetGposeCharacterFromObjectTableByNameAsync(player.Name.TextValue, true).ConfigureAwait(false);
            if (player == null) return null;
            worldData = await _brio.GetTransformAsync(player.Address).ConfigureAwait(false);
        }
        else
        {
            var rotQuaternion = Quaternion.CreateFromAxisAngle(new Vector3(0, 1, 0), player.Rotation);
            worldData = new WorldData
            {
                PositionX = player.Position.X,
                PositionY = player.Position.Y,
                PositionZ = player.Position.Z,
                RotationW = rotQuaternion.W,
                RotationX = rotQuaternion.X,
                RotationY = rotQuaternion.Y,
                RotationZ = rotQuaternion.Z,
                ScaleX = 1,
                ScaleY = 1,
                ScaleZ = 1,
            };
        }

        worldData.LocationInfo = await _dalamudUtil.GetMapDataAsync().ConfigureAwait(false);
        return worldData;
    }

    private async Task UpdateMemberWisps(WorldData worldData)
    {
        foreach (var member in _session.Members)
        {
            if (!member.HasWorldDataUpdate || _dalamudUtil.IsInGpose || member.WorldData == null)
                continue;

            var entryWorldData = member.WorldData.Value;

            if (worldData.LocationInfo.MapId == entryWorldData.LocationInfo.MapId
                && worldData.LocationInfo.DivisionId == entryWorldData.LocationInfo.DivisionId
                && (worldData.LocationInfo.HouseId != entryWorldData.LocationInfo.HouseId
                    || worldData.LocationInfo.WardId != entryWorldData.LocationInfo.WardId
                    || entryWorldData.LocationInfo.ServerId != worldData.LocationInfo.ServerId))
            {
                if (member.SpawnedVfxId == null)
                {
                    member.LastWorldPosition = new Vector3(entryWorldData.PositionX, entryWorldData.PositionY, entryWorldData.PositionZ);
                    member.SpawnedVfxId = await Service.RunOnFrameworkAsync(() => _vfxSpawnManager.SpawnObject(member.LastWorldPosition.Value,
                        Quaternion.Identity, Vector3.One, 0.5f, 0.1f, 0.5f, 0.9f)).ConfigureAwait(false);
                }
                else
                {
                    var newPosition = new Vector3(entryWorldData.PositionX, entryWorldData.PositionY, entryWorldData.PositionZ);
                    var currentTarget = member.TargetWorldPosition ?? member.LastWorldPosition;
                    if (newPosition != currentTarget)
                    {
                        member.UpdateStart = DateTime.UtcNow;
                        member.TargetWorldPosition = newPosition;
                    }
                }
            }
            else
            {
                await Service.RunOnFrameworkAsync(() => _vfxSpawnManager.DespawnObject(member.SpawnedVfxId)).ConfigureAwait(false);
                member.SpawnedVfxId = null;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _loopCts.Cancel();
        _backgroundTasks.StopAccepting();
        _backgroundTasks.StopSynchronously(_logger, TimeSpan.FromSeconds(2), nameof(GposeWorldSync));
        _loopCts.Dispose();
    }
}
