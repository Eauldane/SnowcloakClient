using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Snowcloak.API.Dto.CharaData;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using System.Numerics;
using System.Text.Json.Nodes;
﻿using Brio.API;

using ElezenTools.Services;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerBrio : IIpcCaller
{
    private const string IpcName = "Brio";
    private const string RequiredVersion = "IPC 3.0";
    private const IpcCapability SupportedCapabilities = IpcCapability.GposeActors | IpcCapability.Pose;

    private readonly IDalamudPluginInterface _pi;
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


    public IpcStatus Status { get; private set; } = IpcStatus.Missing(IpcName, IpcRole.Special, SupportedCapabilities, RequiredVersion);
    public bool APIAvailable => Status.IsAvailable;

    public IpcCallerBrio(ILogger<IpcCallerBrio> logger, IDalamudPluginInterface dalamudPluginInterface,
        DalamudUtilService dalamudUtilService)
    {
        _pi = dalamudPluginInterface;
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
            var statusVersion = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"IPC {version.Item1}.{version.Item2}");
            Status = version is { Item1: 3, Item2: >= 0 }
                ? IpcStatus.Available(IpcName, IpcRole.Special, SupportedCapabilities, statusVersion)
                : IpcStatus.VersionMismatch(IpcName, IpcRole.Special, SupportedCapabilities, statusVersion, RequiredVersion);
        }
        catch (Exception ex)
        {
            var plugin = IpcPluginProbe.Find(_pi, IpcName);
            Status = plugin switch
            {
                { IsInstalled: false } => IpcStatus.Missing(IpcName, IpcRole.Special, SupportedCapabilities, RequiredVersion),
                { IsLoaded: false } => IpcStatus.Disabled(IpcName, IpcRole.Special, SupportedCapabilities, plugin.Version?.ToString(), "plugin is installed but not loaded"),
                _ => IpcStatus.Error(IpcName, IpcRole.Special, SupportedCapabilities, ex.Message, plugin.Version?.ToString(), RequiredVersion),
            };
        }
    }

    public async Task<IGameObject?> SpawnActorAsync()
    {
        if (!APIAvailable) return null;
        _logger.LogDebug("Spawning Brio Actor");
        return await Service.RunOnFrameworkAsync(() => _spawnActor.Invoke(Brio.API.Enums.SpawnFlags.Default, true)).ConfigureAwait(false);
    }

    public async Task<bool> DespawnActorAsync(nint address)
    {
        if (!APIAvailable) return false;
        var gameObject = await _dalamudUtilService.CreateGameObjectAsync(address).ConfigureAwait(false);
        if (gameObject == null) return false;
        _logger.LogDebug("Despawning Brio Actor {actor}", gameObject.Name.TextValue);
        return await Service.RunOnFrameworkAsync(() => _despawnActor.Invoke(gameObject)).ConfigureAwait(false);
    }

    public async Task<bool> ApplyTransformAsync(nint address, WorldData data)
    {
        if (!APIAvailable) return false;
        var gameObject = await _dalamudUtilService.CreateGameObjectAsync(address).ConfigureAwait(false);
        if (gameObject == null) return false;
        _logger.LogDebug("Applying Transform to Actor {actor}", gameObject.Name.TextValue);

        return await Service.RunOnFrameworkAsync(() => _setModelTransform.Invoke(gameObject,
            new Vector3(data.PositionX, data.PositionY, data.PositionZ),
            new Quaternion(data.RotationX, data.RotationY, data.RotationZ, data.RotationW),
            new Vector3(data.ScaleX, data.ScaleY, data.ScaleZ), false)).ConfigureAwait(false);
    }

    public async Task<WorldData> GetTransformAsync(nint address)
    {
        if (!APIAvailable) return default;
        var gameObject = await _dalamudUtilService.CreateGameObjectAsync(address).ConfigureAwait(false);
        if (gameObject == null) return default;
        var data = await Service.RunOnFrameworkAsync(() => _getModelTransform.Invoke(gameObject)).ConfigureAwait(false);
        if (data.Item1 == null || data.Item2 == null || data.Item3 == null) return default;

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

        return await Service.RunOnFrameworkAsync(() => _getPoseAsJson.Invoke(gameObject)).ConfigureAwait(false);
    }

    public async Task<bool> SetPoseAsync(nint address, string pose)
    {
        if (!APIAvailable) return false;
        var gameObject = await _dalamudUtilService.CreateGameObjectAsync(address).ConfigureAwait(false);
        if (gameObject == null) return false;
        _logger.LogDebug("Setting Pose to Actor {actor}", gameObject.Name.TextValue);

        var applicablePose = JsonNode.Parse(pose)!;
        var currentPose = await Service.RunOnFrameworkAsync(() => _getPoseAsJson.Invoke(gameObject)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(currentPose))
        {
            return false;
        }

        var currentPoseNode = JsonNode.Parse(currentPose);
        var modelDifference = currentPoseNode?["ModelDifference"];
        if (modelDifference == null)
        {
            return false;
        }

        applicablePose["ModelDifference"] = JsonNode.Parse(modelDifference.ToJsonString());

        await Service.RunOnFrameworkAsync(() =>
        {
            _freezeActor.Invoke(gameObject);
            _freezePhysics.Invoke();
        }).ConfigureAwait(false);
        return await Service.RunOnFrameworkAsync(() => _loadPoseFromJson.Invoke(gameObject, applicablePose.ToJsonString(), false)).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}
