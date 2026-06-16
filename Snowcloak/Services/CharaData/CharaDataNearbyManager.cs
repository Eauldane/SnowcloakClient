using ElezenTools.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using Snowcloak.Core.CharaData;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.Interop;
using Snowcloak.Configuration;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using System.Numerics;

namespace Snowcloak.Services.CharaData;

public sealed record NearbyCharaDataEntry(float Direction, float Distance);

public sealed class CharaDataNearbyManager : DisposableMediatorSubscriberBase
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly Dictionary<PoseEntryExtended, NearbyCharaDataEntry> _nearbyData = [];
    private readonly Dictionary<PoseEntryExtended, Guid> _poseVfx = [];
    private readonly NotesStore _notesStore;
    private readonly CharaDataConfigService _charaDataConfigService;
    private readonly Dictionary<UserData, List<CharaDataMetaInfoExtendedDto>> _metaInfoCache = [];
    private readonly VfxSpawnManager _vfxSpawnManager;
    private Task? _filterEntriesRunningTask;
    private (Guid VfxId, PoseEntryExtended Pose)? _hoveredVfx;
    private DateTime _lastExecutionTime = DateTime.UtcNow;
    private readonly SemaphoreSlim _sharedDataUpdateSemaphore = new(1, 1);
    private readonly IFrameTickHandle _tick;
    private readonly IFrameTickHandle _cutsceneTick;
    public CharaDataNearbyManager(ILogger<CharaDataNearbyManager> logger, SnowMediator mediator,
        DalamudUtilService dalamudUtilService, VfxSpawnManager vfxSpawnManager,
        NotesStore notesStore,
        CharaDataConfigService charaDataConfigService, IFrameScheduler frameScheduler) : base(logger, mediator)
    {
        _tick = frameScheduler.Register("CharaDataNearby", TickInterval.EveryFrame, TickPriority.Low, HandleFrameworkUpdate,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
        _cutsceneTick = frameScheduler.RegisterGated("CharaDataNearbyCutscene", TickInterval.EveryFrame, TickPriority.Low, HandleFrameworkUpdate,
            [FrameGates.Dead], [FrameGates.Cutscene]);
        _dalamudUtilService = dalamudUtilService;
        _vfxSpawnManager = vfxSpawnManager;
        _notesStore = notesStore;
        _charaDataConfigService = charaDataConfigService;
        mediator.Subscribe<GposeStartMessage>(this, (_) => ClearAllVfx());
    }

    public bool ComputeNearbyData { get; set; }

    public IDictionary<PoseEntryExtended, NearbyCharaDataEntry> NearbyData => _nearbyData;

    public string UserNoteFilter { get; set; } = string.Empty;

    public void UpdateSharedData(Dictionary<string, CharaDataMetaInfoExtendedDto?> newData)
    {
        _sharedDataUpdateSemaphore.Wait();
        try
        {
            _metaInfoCache.Clear();
            foreach (var kvp in newData)
            {
                if (kvp.Value == null) continue;

                if (!_metaInfoCache.TryGetValue(kvp.Value.Uploader, out var list))
                {
                    _metaInfoCache[kvp.Value.Uploader] = list = [];
                }

                list.Add(kvp.Value);
            }
        }
        finally
        {
            _sharedDataUpdateSemaphore.Release();
        }
    }

    internal void SetHoveredVfx(PoseEntryExtended? hoveredPose)
    {
        if (hoveredPose == null && _hoveredVfx == null)
            return;

        if (hoveredPose == null)
        {
            _vfxSpawnManager.DespawnObject(_hoveredVfx!.Value.VfxId);
            _hoveredVfx = null;
            return;
        }

        if (_hoveredVfx == null)
        {
            var vfxGuid = _vfxSpawnManager.SpawnObject(hoveredPose.Position, hoveredPose.Rotation, Vector3.One * 4, 1, 0.2f, 0.2f, 1f);
            if (vfxGuid != null)
                _hoveredVfx = (vfxGuid.Value, hoveredPose);
            return;
        }

        if (hoveredPose != _hoveredVfx!.Value.Pose)
        {
            _vfxSpawnManager.DespawnObject(_hoveredVfx.Value.VfxId);
            var vfxGuid = _vfxSpawnManager.SpawnObject(hoveredPose.Position, hoveredPose.Rotation, Vector3.One * 4, 1, 0.2f, 0.2f, 1f);
            if (vfxGuid != null)
                _hoveredVfx = (vfxGuid.Value, hoveredPose);
        }
    }

    protected override void Dispose(bool disposing)
    {
        _tick.Dispose();
        _cutsceneTick.Dispose();
        base.Dispose(disposing);
        ClearAllVfx();
    }

    private void ClearAllVfx()
    {
        foreach (var vfx in _poseVfx)
        {
            _vfxSpawnManager.DespawnObject(vfx.Value);
        }
        _poseVfx.Clear();
    }

    private async Task FilterEntriesAsync(Vector3 cameraPos, Vector3 cameraLookAt)
    {
        var previousPoses = _nearbyData.Keys.ToList();
        _nearbyData.Clear();

        var ownLocation = await Service.RunOnFrameworkAsync(() => _dalamudUtilService.GetMapData()).ConfigureAwait(false);
        var player = await Service.RunOnFrameworkAsync(() => _dalamudUtilService.GetPlayerCharacter()).ConfigureAwait(false);
        var currentServer = player.CurrentWorld;
        var playerPos = player.Position;

        bool ignoreHousingLimits = _charaDataConfigService.Current.NearbyIgnoreHousingLimitations;
        bool onlyCurrentServer = _charaDataConfigService.Current.NearbyOwnServerOnly;
        bool showOwnData = _charaDataConfigService.Current.NearbyShowOwnData;
        var noteFilter = UserNoteFilter;
        bool hasNoteFilter = !string.IsNullOrWhiteSpace(noteFilter);
        var configuredNote = hasNoteFilter ? (_notesStore.GetNoteForUid(noteFilter) ?? string.Empty) : string.Empty;
        float distanceLimit = _charaDataConfigService.Current.NearbyDistanceFilter;
        KeyValuePair<UserData, List<CharaDataMetaInfoExtendedDto>>[] metaInfoSnapshot;
        await _sharedDataUpdateSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            metaInfoSnapshot = _metaInfoCache.ToArray();
        }
        finally
        {
            _sharedDataUpdateSemaphore.Release();
        }

        var candidates = new List<NearbyPoseCandidate<PoseEntryExtended>>();
        foreach (var data in metaInfoSnapshot)
        {
            foreach (var entry in data.Value)
            {
                if (!entry.HasPoses)
                {
                    continue;
                }

                foreach (var pose in entry.PoseExtended)
                {
                    candidates.Add(new NearbyPoseCandidate<PoseEntryExtended>(
                        pose,
                        data.Key,
                        entry.IsOwnData,
                        pose.HasPoseData,
                        pose.HasWorldData,
                        pose.WorldData?.LocationInfo ?? default,
                        pose.Position));
                }
            }
        }

        var selected = NearbyPoseSelector.Select(candidates, new NearbyPoseContext(
            ownLocation,
            currentServer.RowId,
            playerPos,
            cameraPos,
            cameraLookAt,
            noteFilter,
            configuredNote,
            ignoreHousingLimits,
            onlyCurrentServer,
            showOwnData,
            distanceLimit));

        foreach (var data in selected)
        {
            _nearbyData[data.Pose] = new(data.Direction, data.Distance);
        }

        if (_charaDataConfigService.Current.NearbyDrawWisps && !_dalamudUtilService.IsInGpose && !_dalamudUtilService.IsInCombatOrPerforming)
            await Service.RunOnFrameworkAsync(() => ManageWispsNearby(previousPoses)).ConfigureAwait(false);
    }

    private unsafe void HandleFrameworkUpdate()
    {
        if (_lastExecutionTime.AddSeconds(0.5) > DateTime.UtcNow) return;
        _lastExecutionTime = DateTime.UtcNow;
        if (!ComputeNearbyData && !_charaDataConfigService.Current.NearbyShowAlways)
        {
            if (_nearbyData.Any())
                _nearbyData.Clear();
            if (_poseVfx.Any())
                ClearAllVfx();
            return;
        }

        if (!_charaDataConfigService.Current.NearbyDrawWisps || _dalamudUtilService.IsInGpose || _dalamudUtilService.IsInCombatOrPerforming)
            ClearAllVfx();

        var camera = CameraManager.Instance()->CurrentCamera;
        Vector3 cameraPos = new(camera->Position.X, camera->Position.Y, camera->Position.Z);
        Vector3 lookAt = new(camera->LookAtVector.X, camera->LookAtVector.Y, camera->LookAtVector.Z);

        if (_filterEntriesRunningTask?.IsCompleted != false && _dalamudUtilService.IsLoggedIn)
            _filterEntriesRunningTask = FilterEntriesAsync(cameraPos, lookAt);
    }

    private void ManageWispsNearby(List<PoseEntryExtended> previousPoses)
    {
        foreach (var data in _nearbyData.Keys)
        {
            if (_poseVfx.TryGetValue(data, out var _)) continue;

            Guid? vfxGuid;
            if (data.MetaInfo.IsOwnData)
            {
                vfxGuid = _vfxSpawnManager.SpawnObject(data.Position, data.Rotation, Vector3.One * 2, 0.8f, 0.5f, 0.0f, 0.7f);
            }
            else
            {
                vfxGuid = _vfxSpawnManager.SpawnObject(data.Position, data.Rotation, Vector3.One * 2);
            }
            if (vfxGuid != null)
            {
                _poseVfx[data] = vfxGuid.Value;
            }
        }

        foreach (var data in previousPoses.Except(_nearbyData.Keys))
        {
            if (_poseVfx.Remove(data, out var guid))
            {
                _vfxSpawnManager.DespawnObject(guid);
            }
        }
    }
}
