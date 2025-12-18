using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Snowcloak.API.Dto.CharaData;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using System.Numerics;
using System.Text.Json.Nodes;
﻿using Brio.API;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerBrio : IIpcCaller
{
    private readonly ILogger<IpcCallerBrio> _logger;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly ApiVersion _apiVersion;
    private readonly SpawnActor _spawnActor;
    private readonly DespawnActor _despawnActor;
    private readonly SetModelTransform _setModelTransform;
    private readonly GetModelTransform _getModelTransform;
    private readonly GetPoseAsJson _getPoseAsJson;
    private readonly LoadPoseFromJson _loadPoseFromJson;
    private readonly FreezeActor _freezeActor;
    private readonly FreezePhysics _freezePhysics;


    public bool APIAvailable { get; private set; }

    public IpcCallerBrio(ILogger<IpcCallerBrio> logger, IDalamudPluginInterface dalamudPluginInterface,
        DalamudUtilService dalamudUtilService)
    {
        _logger = logger;
        _dalamudUtilService = dalamudUtilService;
        _apiVersion = new ApiVersion(dalamudPluginInterface);
        _spawnActor = new SpawnActor(dalamudPluginInterface);
        _despawnActor = new DespawnActor(dalamudPluginInterface);
        _setModelTransform = new SetModelTransform(dalamudPluginInterface);
        _getModelTransform = new GetModelTransform(dalamudPluginInterface);
        _getPoseAsJson = new GetPoseAsJson(dalamudPluginInterface);
        _loadPoseFromJson = new LoadPoseFromJson(dalamudPluginInterface);
        _freezeActor = new FreezeActor(dalamudPluginInterface);
        _freezePhysics = new FreezePhysics(dalamudPluginInterface);

        CheckAPI();
    }

    public void CheckAPI()
    {
        try
        {
            var version = _apiVersion.Invoke();
            APIAvailable = (version.Item1 == 2 && version.Item2 >= 0);
        }
        catch
        {
            APIAvailable = false;
        }
    }

    public async Task<IGameObject?> SpawnActorAsync()
    {
        if (!APIAvailable) return null;
        _logger.LogDebug("Spawning Brio Actor");
        return await _dalamudUtilService.RunOnFrameworkThread(() => _spawnActor.Invoke(Brio.API.Enums.SpawnFlags.Default, true)).ConfigureAwait(false);
    }

    public async Task<bool> DespawnActorAsync(nint address)
    {
        if (!APIAvailable) return false;
        var gameObject = await _dalamudUtilService.CreateGameObjectAsync(address).ConfigureAwait(false);
        if (gameObject == null) return false;
        _logger.LogDebug("Despawning Brio Actor {actor}", gameObject.Name.TextValue);
        return await _dalamudUtilService.RunOnFrameworkThread(() => _despawnActor.Invoke(gameObject)).ConfigureAwait(false);
    }

    public async Task<bool> ApplyTransformAsync(nint address, WorldData data)
    {
        if (!APIAvailable) return false;
        var gameObject = await _dalamudUtilService.CreateGameObjectAsync(address).ConfigureAwait(false);
        if (gameObject == null) return false;
        _logger.LogDebug("Applying Transform to Actor {actor}", gameObject.Name.TextValue);

        return await _dalamudUtilService.RunOnFrameworkThread(() => _setModelTransform.Invoke(gameObject,
            new Vector3(data.PositionX, data.PositionY, data.PositionZ),
            new Quaternion(data.RotationX, data.RotationY, data.RotationZ, data.RotationW),
            new Vector3(data.ScaleX, data.ScaleY, data.ScaleZ), false)).ConfigureAwait(false);
    }

    public async Task<WorldData> GetTransformAsync(nint address)
    {
        if (!APIAvailable) return default;
        var gameObject = await _dalamudUtilService.CreateGameObjectAsync(address).ConfigureAwait(false);
        if (gameObject == null) return default;
        var data = await _dalamudUtilService.RunOnFrameworkThread(() => _getModelTransform.Invoke(gameObject)).ConfigureAwait(false);
        if (data.Item1 == null || data.Item2 == null || data.Item3 == null) return default;
        //_logger.LogDebug("Getting Transform from Actor {actor}", gameObject.Name.TextValue);

        return new WorldData()
        {
            PositionX = data.Item1.Value.X,
            PositionY = data.Item1.Value.Y,
            PositionZ = data.Item1.Value.Z,
            RotationX = data.Item2.Value.X,
            RotationY = data.Item2.Value.Y,
            RotationZ = data.Item2.Value.Z,
            RotationW = data.Item2.Value.W,
            ScaleX = data.Item3.Value.X,
            ScaleY = data.Item3.Value.Y,
            ScaleZ = data.Item3.Value.Z
        };
    }

    public async Task<string?> GetPoseAsync(nint address)
    {
        if (!APIAvailable) return null;
        var gameObject = await _dalamudUtilService.CreateGameObjectAsync(address).ConfigureAwait(false);
        if (gameObject == null) return null;
        _logger.LogDebug("Getting Pose from Actor {actor}", gameObject.Name.TextValue);

        return await _dalamudUtilService.RunOnFrameworkThread(() => _getPoseAsJson.Invoke(gameObject)).ConfigureAwait(false);
    }

    public async Task<bool> SetPoseAsync(nint address, string pose)
    {
        if (!APIAvailable) return false;
        var gameObject = await _dalamudUtilService.CreateGameObjectAsync(address).ConfigureAwait(false);
        if (gameObject == null) return false;
        _logger.LogDebug("Setting Pose to Actor {actor}", gameObject.Name.TextValue);

        var applicablePose = JsonNode.Parse(pose)!;
        var currentPose = await _dalamudUtilService.RunOnFrameworkThread(() => _getPoseAsJson.Invoke(gameObject)).ConfigureAwait(false);
        applicablePose["ModelDifference"] = JsonNode.Parse(JsonNode.Parse(currentPose)!["ModelDifference"]!.ToJsonString());

        await _dalamudUtilService.RunOnFrameworkThread(() =>
        {
            _freezeActor.Invoke(gameObject);
            _freezePhysics.Invoke();
        }).ConfigureAwait(false);
        return await _dalamudUtilService.RunOnFrameworkThread(() => _loadPoseFromJson.Invoke(gameObject, applicablePose.ToJsonString(), false)).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}
