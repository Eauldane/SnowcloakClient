using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using System.Collections.Concurrent;

using ElezenTools.Services;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerPenumbra : DisposableMediatorSubscriberBase, IPenumbraIpc
{
    private const string IpcName = "Penumbra";
    private const string RequiredVersion = "1.6.1.10";
    private static readonly Version MinimumPluginVersion = new(1, 6, 1, 10);
    private const IpcCapability SupportedCapabilities = IpcCapability.ModFiles
        | IpcCapability.MetaManipulations
        | IpcCapability.Redraw
        | IpcCapability.ResourcePaths;

    private enum ModBackend
    {
        None,
        Penumbra,
    }

    private readonly IDalamudPluginInterface _pi;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SnowMediator _snowMediator;
    private readonly RedrawManager _redrawManager;
    private bool _shownPenumbraUnavailable;
    private ModBackend _backend = ModBackend.None;
    private string? _penumbraModDirectory;

    public string? ModDirectory
    {
        get => _penumbraModDirectory;
        private set
        {
            if (!string.Equals(_penumbraModDirectory, value, StringComparison.Ordinal))
            {
                _penumbraModDirectory = value;
                _snowMediator.Publish(new PenumbraDirectoryChangedMessage(_penumbraModDirectory));
            }
        }
    }

    private readonly ConcurrentDictionary<IntPtr, bool> _penumbraRedrawRequests = new();

    private readonly EventSubscriber _penumbraDispose;
    private readonly EventSubscriber<nint, string, string> _penumbraGameObjectResourcePathResolved;
    private readonly EventSubscriber _penumbraInit;
    private readonly EventSubscriber<ModSettingChange, Guid, string, bool> _penumbraModSettingChanged;
    private readonly EventSubscriber<nint, int> _penumbraObjectIsRedrawn;

    private readonly AddTemporaryMod _penumbraAddTemporaryMod;
    private readonly AssignTemporaryCollection _penumbraAssignTemporaryCollection;
    private readonly ConvertTextureFile _penumbraConvertTextureFile;
    private readonly CreateTemporaryCollection _penumbraCreateNamedTemporaryCollection;
    private readonly GetEnabledState _penumbraEnabled;
    private readonly GetPlayerMetaManipulations _penumbraGetMetaManipulations;
    private readonly RedrawObject _penumbraRedraw;
    private readonly DeleteTemporaryCollection _penumbraRemoveTemporaryCollection;
    private readonly RemoveTemporaryMod _penumbraRemoveTemporaryMod;
    private readonly GetModDirectory _penumbraResolveModDir;
    private readonly ResolvePlayerPathsAsync _penumbraResolvePaths;
    private readonly GetGameObjectResourcePaths _penumbraResourcePaths;

    public IpcCallerPenumbra(ILogger<IpcCallerPenumbra> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil,
        SnowMediator snowMediator, RedrawManager redrawManager) : base(logger, snowMediator)
    {
        _pi = pi;
        _dalamudUtil = dalamudUtil;
        _snowMediator = snowMediator;
        _redrawManager = redrawManager;

        _penumbraInit = Initialized.Subscriber(pi, PenumbraInit);
        _penumbraDispose = Disposed.Subscriber(pi, PenumbraDispose);
        _penumbraResolveModDir = new GetModDirectory(pi);
        _penumbraRedraw = new RedrawObject(pi);
        _penumbraObjectIsRedrawn = GameObjectRedrawn.Subscriber(pi, (address, objectIndex) =>
        {
            if (_backend == ModBackend.Penumbra)
            {
                RedrawEvent(address, objectIndex);
            }
        });
        _penumbraGetMetaManipulations = new GetPlayerMetaManipulations(pi);
        _penumbraRemoveTemporaryMod = new RemoveTemporaryMod(pi);
        _penumbraAddTemporaryMod = new AddTemporaryMod(pi);
        _penumbraCreateNamedTemporaryCollection = new CreateTemporaryCollection(pi);
        _penumbraRemoveTemporaryCollection = new DeleteTemporaryCollection(pi);
        _penumbraAssignTemporaryCollection = new AssignTemporaryCollection(pi);
        _penumbraResolvePaths = new ResolvePlayerPathsAsync(pi);
        _penumbraEnabled = new GetEnabledState(pi);
        _penumbraModSettingChanged = ModSettingChanged.Subscriber(pi, (change, _, _, _) =>
        {
            if (_backend == ModBackend.Penumbra && change == ModSettingChange.EnableState)
            {
                _snowMediator.Publish(new PenumbraModSettingChangedMessage());
            }
        });
        _penumbraConvertTextureFile = new ConvertTextureFile(pi);
        _penumbraResourcePaths = new GetGameObjectResourcePaths(pi);
        _penumbraGameObjectResourcePathResolved = GameObjectResourcePathResolved.Subscriber(pi, (ptr, gamePath, resolvedPath) =>
        {
            if (_backend == ModBackend.Penumbra)
            {
                ResourceLoaded(ptr, gamePath, resolvedPath);
            }
        });

        CheckAPI();
        CheckModDirectory();

        Mediator.Subscribe<PenumbraRedrawCharacterMessage>(this, msg => InvokeRedraw(msg.Character.ObjectIndex, RedrawType.AfterGPose));
        Mediator.Subscribe<DalamudLoginMessage>(this, _ => _shownPenumbraUnavailable = false);
    }

    public IpcStatus Status { get; private set; } = IpcStatus.Missing(IpcName, IpcRole.Required, SupportedCapabilities, RequiredVersion);
    public bool APIAvailable => Status.IsAvailable;

    public void CheckAPI()
    {
        var penumbraPlugin = IpcPluginProbe.Find(_pi, IpcName);
        var version = penumbraPlugin.Version?.ToString();

        if (!penumbraPlugin.IsInstalled)
        {
            Status = IpcStatus.Missing(IpcName, IpcRole.Required, SupportedCapabilities, RequiredVersion);
            _backend = ModBackend.None;
        }
        else if (!penumbraPlugin.IsLoaded)
        {
            Status = IpcStatus.Disabled(IpcName, IpcRole.Required, SupportedCapabilities, version, "plugin is installed but not loaded");
            _backend = ModBackend.None;
        }
        else if (penumbraPlugin.Version!.CompareTo(MinimumPluginVersion) < 0)
        {
            Status = IpcStatus.VersionMismatch(IpcName, IpcRole.Required, SupportedCapabilities, version, RequiredVersion);
            _backend = ModBackend.None;
        }
        else
        {
            try
            {
                var penumbraAvailable = _penumbraEnabled.Invoke();
                Status = penumbraAvailable
                    ? IpcStatus.Available(IpcName, IpcRole.Required, SupportedCapabilities, version)
                    : IpcStatus.Disabled(IpcName, IpcRole.Required, SupportedCapabilities, version, "Penumbra reports itself inactive");
                _backend = penumbraAvailable ? ModBackend.Penumbra : ModBackend.None;
            }
            catch (Exception ex)
            {
                Status = IpcStatus.Error(IpcName, IpcRole.Required, SupportedCapabilities, ex.Message, version, RequiredVersion);
                _backend = ModBackend.None;
            }
        }
        _shownPenumbraUnavailable = _shownPenumbraUnavailable && !APIAvailable;

        if (!APIAvailable && !_shownPenumbraUnavailable)
        {
            _shownPenumbraUnavailable = true;
            _snowMediator.Publish(new NotificationMessage(
                "Penumbra inactive",
                "Penumbra is not active. Enable Penumbra to continue to use Snowcloak.",
                NotificationType.Error));
        }
    }

    public void CheckModDirectory()
    {
        ModDirectory = _backend == ModBackend.Penumbra
            ? _penumbraResolveModDir.Invoke().ToLowerInvariant()
            : string.Empty;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _redrawManager.Cancel();

        _penumbraModSettingChanged.Dispose();
        _penumbraGameObjectResourcePathResolved.Dispose();
        _penumbraDispose.Dispose();
        _penumbraInit.Dispose();
        _penumbraObjectIsRedrawn.Dispose();
    }

    public async Task AssignTemporaryCollectionAsync(ILogger logger, Guid collName, int idx)
    {
        if (!APIAvailable)
        {
            return;
        }

        await Service.RunOnFrameworkAsync(() =>
        {
            var retAssign = _penumbraAssignTemporaryCollection.Invoke(collName, idx, forceAssignment: true);
            logger.LogTrace("Assigning Temp Collection {collName} to index {idx}, Success: {ret}", collName, idx, retAssign);
            return collName;
        }).ConfigureAwait(false);
    }

    public async Task ConvertTextureFiles(ILogger logger, Dictionary<string, (TextureType TextureType, string[] Duplicates)> textures, IProgress<(string, int)> progress, CancellationToken token)
    {
        if (!APIAvailable)
        {
            return;
        }

        _snowMediator.Publish(new HaltScanMessage(nameof(ConvertTextureFiles)));
        var currentTexture = 0;
        foreach (var texture in textures)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            progress.Report((texture.Key, ++currentTexture));
            logger.LogInformation("Converting Texture {path} to {type}", texture.Key, texture.Value.TextureType);

            var convertTask = _penumbraConvertTextureFile.Invoke(texture.Key, texture.Key, texture.Value.TextureType, mipMaps: true);
            await convertTask.ConfigureAwait(false);
            var converted = convertTask.IsCompletedSuccessfully;

            if (converted && texture.Value.Duplicates.Any())
            {
                foreach (var duplicatedTexture in texture.Value.Duplicates)
                {
                    logger.LogInformation("Migrating duplicate {dup}", duplicatedTexture);
                    try
                    {
                        File.Copy(texture.Key, duplicatedTexture, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to copy duplicate {dup}", duplicatedTexture);
                    }
                }
            }
        }

        _snowMediator.Publish(new ResumeScanMessage(nameof(ConvertTextureFiles)));

        await Service.RunOnFrameworkAsync(async () =>
        {
            var gameObject = await _dalamudUtil.CreateGameObjectAsync(await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false)).ConfigureAwait(false);
            if (gameObject != null)
            {
                InvokeRedraw(gameObject.ObjectIndex, RedrawType.Redraw);
            }
        }).ConfigureAwait(false);
    }

    public async Task<Guid> CreateTemporaryCollectionAsync(ILogger logger, string uid)
    {
        if (!APIAvailable)
        {
            return Guid.Empty;
        }

        return await Service.RunOnFrameworkAsync(() =>
        {
            var random = new Random();
            var collName = "Snowcloak_" + uid + random.Next();

            Guid collId;
            var penumbraEc = _penumbraCreateNamedTemporaryCollection.Invoke(uid + random.Next(), collName, out collId);
            logger.LogTrace("Creating Temp Collection {collName}, GUID: {collId}", collName, collId);
            if (penumbraEc != PenumbraApiEc.Success)
            {
                logger.LogError("Failed to create temporary collection");
            }

            return collId;
        }).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, HashSet<string>>?> GetCharacterData(ILogger logger, IGameObjectHandle handler)
    {
        if (!APIAvailable)
        {
            return null;
        }

        return await Service.RunOnFrameworkAsync(() =>
        {
            logger.LogTrace("Calling resource path IPC via {backend}", _backend);
            var idx = handler.ObjectIndex;
            if (idx == null)
            {
                return null;
            }

            return _penumbraResourcePaths.Invoke(idx.Value)[0];
        }).ConfigureAwait(false);
    }

    public string GetMetaManipulations()
    {
        if (!APIAvailable)
        {
            return string.Empty;
        }

        return _penumbraGetMetaManipulations.Invoke();
    }

    public async Task RedrawAsync(ILogger logger, IGameObjectHandle handler, Guid applicationId, CancellationToken token)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning)
        {
            return;
        }

        await _redrawManager.RunWithRedrawSlotAsync(logger, (GameObjectHandler)handler, applicationId, chara =>
        {
            logger.LogDebug("[{appid}] Calling redraw on {backend}", applicationId, _backend);
            InvokeRedraw(chara.ObjectIndex, RedrawType.Redraw);
        }, token).ConfigureAwait(false);
    }

    public async Task RemoveTemporaryCollectionAsync(ILogger logger, Guid applicationId, Guid collId)
    {
        if (!APIAvailable)
        {
            return;
        }

        await Service.RunOnFrameworkAsync(() =>
        {
            using var scope = logger.BeginScope("{ApplicationId}", applicationId);
            logger.LogTrace("Removing temp collection for {collId}", collId);
            var ret = _penumbraRemoveTemporaryCollection.Invoke(collId);
            logger.LogTrace("RemoveTemporaryCollection: {ret}", ret);
        }).ConfigureAwait(false);
    }

    public async Task<(string[] forward, string[][] reverse)> ResolvePathsAsync(string[] forward, string[] reverse)
        => await _penumbraResolvePaths.Invoke(forward, reverse).ConfigureAwait(false);

    public async Task SetManipulationDataAsync(ILogger logger, Guid applicationId, Guid collId, string manipulationData)
    {
        if (!APIAvailable)
        {
            return;
        }

        await Service.RunOnFrameworkAsync(() =>
        {
            using var scope = logger.BeginScope("{ApplicationId}", applicationId);
            logger.LogTrace("Manip: {data}", manipulationData);
            var retAdd = _penumbraAddTemporaryMod.Invoke("SnowChara_Meta", collId, [], manipulationData, 0);
            logger.LogTrace("Setting temp meta mod for {collId}, Success: {ret}", collId, retAdd);
        }).ConfigureAwait(false);
    }

    public async Task SetTemporaryModsAsync(ILogger logger, Guid applicationId, Guid collId, Dictionary<string, string> modPaths)
    {
        if (!APIAvailable)
        {
            return;
        }

        await Service.RunOnFrameworkAsync(() =>
        {
            using var scope = logger.BeginScope("{ApplicationId}", applicationId);
            foreach (var mod in modPaths)
            {
                logger.LogTrace("Change: {from} => {to}", mod.Key, mod.Value);
            }

            var retRemove = _penumbraRemoveTemporaryMod.Invoke("SnowChara_Files", collId, 0);
            logger.LogTrace("Removing temp files mod for {collId}, Success: {ret}", collId, retRemove);
            var retAdd = _penumbraAddTemporaryMod.Invoke("SnowChara_Files", collId, modPaths, string.Empty, 0);
            logger.LogTrace("Setting temp files mod for {collId}, Success: {ret}", collId, retAdd);
        }).ConfigureAwait(false);
    }

    private void InvokeRedraw(int? objectIndex, RedrawType setting)
    {
        if (objectIndex == null)
        {
            return;
        }

        if (_backend == ModBackend.Penumbra)
        {
            _penumbraRedraw.Invoke(objectIndex.Value, setting);
        }
    }

    private void RedrawEvent(IntPtr objectAddress, int objectTableIndex)
    {
        var wasRequested = false;
        if (_penumbraRedrawRequests.TryGetValue(objectAddress, out var redrawRequest))
        {
            wasRequested = redrawRequest;
            if (redrawRequest)
            {
                _penumbraRedrawRequests[objectAddress] = false;
            }
        }

        _snowMediator.Publish(new PenumbraRedrawMessage(objectAddress, objectTableIndex, wasRequested));
    }

    private void ResourceLoaded(IntPtr ptr, string arg1, string arg2)
    {
        if (ptr != IntPtr.Zero && string.Compare(arg1, arg2, ignoreCase: true, System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            _snowMediator.Publish(new PenumbraResourceLoadMessage(ptr, arg1, arg2));
        }
    }

    private void PenumbraDispose()
    {
        if (_backend == ModBackend.Penumbra)
        {
            _redrawManager.Cancel();
            _snowMediator.Publish(new PenumbraDisposedMessage());
        }
    }

    private void PenumbraInit()
    {
        if (_backend == ModBackend.Penumbra)
        {
            Status = IpcStatus.Available(IpcName, IpcRole.Required, SupportedCapabilities, Status.Version);
            ModDirectory = _penumbraResolveModDir.Invoke();
            _snowMediator.Publish(new PenumbraInitializedMessage());
            InvokeRedraw(0, RedrawType.Redraw);
        }
    }
}
