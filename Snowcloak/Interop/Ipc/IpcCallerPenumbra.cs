using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ElezenTools.Services;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using System.Collections.Concurrent;

namespace Snowcloak.Interop.Ipc;

public sealed class IpcCallerPenumbra : DisposableMediatorSubscriberBase, IIpcCaller
{
    private enum ModBackend
    {
        None,
        Penumbra,
        Weave,
    }

    private const string WeaveInternalName = "Weave";
    private const string WeaveInitializedLabel = "Weave.Initialized.V1";
    private const string WeaveDisposedLabel = "Weave.Disposed.V1";
    private const string WeaveResourceResolvedLabel = "Weave.GameObjectResourcePathResolved.V1";
    private const string WeaveModSettingsChangedLabel = "Weave.ModSettingsChanged.V1";
    private const string WeaveGameObjectRedrawnLabel = "Weave.GameObjectRedrawn.V1";
    private const string WeaveGetModDirectoryLabel = "Weave.GetModDirectory.V1";
    private const string WeaveGetEnabledStateLabel = "Weave.GetEnabledState.V1";
    private const string WeaveCreateTemporaryCollectionLabel = "Weave.CreateTemporaryCollection.V1";
    private const string WeaveDeleteTemporaryCollectionLabel = "Weave.DeleteTemporaryCollection.V1";
    private const string WeaveAssignTemporaryCollectionLabel = "Weave.AssignTemporaryCollection.V1";
    private const string WeaveAddTemporaryModLabel = "Weave.AddTemporaryMod.V1";
    private const string WeaveRemoveTemporaryModLabel = "Weave.RemoveTemporaryMod.V1";
    private const string WeaveResolvePathsLabel = "Weave.ResolvePaths.V1";
    private const string WeaveGetPlayerMetaManipulationsLabel = "Weave.GetPlayerMetaManipulations.V1";
    private const string WeaveGetGameObjectResourcePathsLabel = "Weave.GetGameObjectResourcePaths.V1";
    private const string WeaveRedrawObjectLabel = "Weave.RedrawObject.V1";
    private const string WeaveConvertTextureFileLabel = "Weave.ConvertTextureFile.V1";

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

    private readonly ICallGateSubscriber<object> _weaveInit;
    private readonly ICallGateSubscriber<object> _weaveDispose;
    private readonly ICallGateSubscriber<nint, string, string, object> _weaveResourceResolved;
    private readonly ICallGateSubscriber<object> _weaveModSettingChanged;
    private readonly ICallGateSubscriber<nint, int, object> _weaveObjectRedrawn;
    private readonly ICallGateSubscriber<string> _weaveResolveModDir;
    private readonly ICallGateSubscriber<bool> _weaveEnabled;
    private readonly ICallGateSubscriber<string, Guid> _weaveCreateTemporaryCollection;
    private readonly ICallGateSubscriber<Guid, bool> _weaveDeleteTemporaryCollection;
    private readonly ICallGateSubscriber<Guid, int, bool, bool> _weaveAssignTemporaryCollection;
    private readonly ICallGateSubscriber<string, Guid, Dictionary<string, string>, string, int, bool> _weaveAddTemporaryMod;
    private readonly ICallGateSubscriber<string, Guid, bool> _weaveRemoveTemporaryMod;
    private readonly ICallGateSubscriber<string[], string[], (string[], string[][])> _weaveResolvePaths;
    private readonly ICallGateSubscriber<string> _weaveGetMetaManipulations;
    private readonly ICallGateSubscriber<int, Dictionary<string, HashSet<string>>[]> _weaveResourcePaths;
    private readonly ICallGateSubscriber<int, int, bool> _weaveRedraw;
    private readonly ICallGateSubscriber<string, string, string, bool, bool> _weaveConvertTextureFile;

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

        _weaveInit = pi.GetIpcSubscriber<object>(WeaveInitializedLabel);
        _weaveDispose = pi.GetIpcSubscriber<object>(WeaveDisposedLabel);
        _weaveResourceResolved = pi.GetIpcSubscriber<nint, string, string, object>(WeaveResourceResolvedLabel);
        _weaveModSettingChanged = pi.GetIpcSubscriber<object>(WeaveModSettingsChangedLabel);
        _weaveObjectRedrawn = pi.GetIpcSubscriber<nint, int, object>(WeaveGameObjectRedrawnLabel);
        _weaveResolveModDir = pi.GetIpcSubscriber<string>(WeaveGetModDirectoryLabel);
        _weaveEnabled = pi.GetIpcSubscriber<bool>(WeaveGetEnabledStateLabel);
        _weaveCreateTemporaryCollection = pi.GetIpcSubscriber<string, Guid>(WeaveCreateTemporaryCollectionLabel);
        _weaveDeleteTemporaryCollection = pi.GetIpcSubscriber<Guid, bool>(WeaveDeleteTemporaryCollectionLabel);
        _weaveAssignTemporaryCollection = pi.GetIpcSubscriber<Guid, int, bool, bool>(WeaveAssignTemporaryCollectionLabel);
        _weaveAddTemporaryMod = pi.GetIpcSubscriber<string, Guid, Dictionary<string, string>, string, int, bool>(WeaveAddTemporaryModLabel);
        _weaveRemoveTemporaryMod = pi.GetIpcSubscriber<string, Guid, bool>(WeaveRemoveTemporaryModLabel);
        _weaveResolvePaths = pi.GetIpcSubscriber<string[], string[], (string[], string[][])>(WeaveResolvePathsLabel);
        _weaveGetMetaManipulations = pi.GetIpcSubscriber<string>(WeaveGetPlayerMetaManipulationsLabel);
        _weaveResourcePaths = pi.GetIpcSubscriber<int, Dictionary<string, HashSet<string>>[]>(WeaveGetGameObjectResourcePathsLabel);
        _weaveRedraw = pi.GetIpcSubscriber<int, int, bool>(WeaveRedrawObjectLabel);
        _weaveConvertTextureFile = pi.GetIpcSubscriber<string, string, string, bool, bool>(WeaveConvertTextureFileLabel);

        _weaveInit.Subscribe(OnWeaveInit);
        _weaveDispose.Subscribe(OnWeaveDispose);
        _weaveResourceResolved.Subscribe(OnWeaveResourceLoaded);
        _weaveModSettingChanged.Subscribe(OnWeaveModSettingChanged);
        _weaveObjectRedrawn.Subscribe(OnWeaveRedrawEvent);

        CheckAPI();
        CheckModDirectory();

        Mediator.Subscribe<PenumbraRedrawCharacterMessage>(this, msg => InvokeRedraw(msg.Character.ObjectIndex, RedrawType.AfterGPose));
        Mediator.Subscribe<DalamudLoginMessage>(this, _ => _shownPenumbraUnavailable = false);
    }

    public bool APIAvailable { get; private set; }

    public void CheckAPI()
    {
        var penumbraPlugin = _pi.InstalledPlugins.FirstOrDefault(p => string.Equals(p.InternalName, "Penumbra", StringComparison.OrdinalIgnoreCase));
        var weavePlugin = _pi.InstalledPlugins.FirstOrDefault(p => string.Equals(p.InternalName, WeaveInternalName, StringComparison.OrdinalIgnoreCase));

        var penumbraAvailable = false;
        if ((penumbraPlugin?.Version ?? new Version(0, 0, 0, 0)) >= new Version(1, 2, 0, 22))
        {
            try
            {
                penumbraAvailable = _penumbraEnabled.Invoke();
            }
            catch
            {
                penumbraAvailable = false;
            }
        }

        var weaveAvailable = false;
        if (weavePlugin is not null)
        {
            try
            {
                weaveAvailable = _weaveEnabled.InvokeFunc();
            }
            catch
            {
                weaveAvailable = false;
            }
        }

        _backend = penumbraAvailable
            ? ModBackend.Penumbra
            : weaveAvailable
                ? ModBackend.Weave
                : ModBackend.None;

        APIAvailable = _backend != ModBackend.None;
        _shownPenumbraUnavailable = _shownPenumbraUnavailable && !APIAvailable;

        if (!APIAvailable && !_shownPenumbraUnavailable)
        {
            _shownPenumbraUnavailable = true;
            _snowMediator.Publish(new NotificationMessage(
                "Penumbra inactive",
                "Neither Penumbra nor Weave is active. Enable Penumbra or install Weave to continue to use Snowcloak.",
                NotificationType.Error));
        }
    }

    public void CheckModDirectory()
    {
        ModDirectory = _backend switch
        {
            ModBackend.Penumbra => _penumbraResolveModDir.Invoke().ToLowerInvariant(),
            ModBackend.Weave => _weaveResolveModDir.InvokeFunc().ToLowerInvariant(),
            _ => string.Empty,
        };
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
        _weaveInit.Unsubscribe(OnWeaveInit);
        _weaveDispose.Unsubscribe(OnWeaveDispose);
        _weaveResourceResolved.Unsubscribe(OnWeaveResourceLoaded);
        _weaveModSettingChanged.Unsubscribe(OnWeaveModSettingChanged);
        _weaveObjectRedrawn.Unsubscribe(OnWeaveRedrawEvent);
    }

    public async Task AssignTemporaryCollectionAsync(ILogger logger, Guid collName, int idx)
    {
        if (!APIAvailable)
        {
            return;
        }

        await Service.UseFramework(() =>
        {
            if (_backend == ModBackend.Penumbra)
            {
                var retAssign = _penumbraAssignTemporaryCollection.Invoke(collName, idx, forceAssignment: true);
                logger.LogTrace("Assigning Temp Collection {collName} to index {idx}, Success: {ret}", collName, idx, retAssign);
            }
            else
            {
                var retAssign = _weaveAssignTemporaryCollection.InvokeFunc(collName, idx, true);
                logger.LogTrace("Assigning Weave Temp Collection {collName} to index {idx}, Success: {ret}", collName, idx, retAssign);
            }

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

            var converted = false;
            if (_backend == ModBackend.Penumbra)
            {
                var convertTask = _penumbraConvertTextureFile.Invoke(texture.Key, texture.Key, texture.Value.TextureType, mipMaps: true);
                await convertTask.ConfigureAwait(false);
                converted = convertTask.IsCompletedSuccessfully;
            }
            else
            {
                converted = _weaveConvertTextureFile.InvokeFunc(texture.Key, texture.Key, texture.Value.TextureType.ToString(), true);
            }

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

        await Service.UseFramework(async () =>
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

        return await Service.UseFramework(() =>
        {
            var random = new Random();
            var collName = "Snowcloak_" + uid + random.Next();

            if (_backend == ModBackend.Penumbra)
            {
                Guid collId;
                var penumbraEc = _penumbraCreateNamedTemporaryCollection.Invoke(uid + random.Next(), collName, out collId);
                logger.LogTrace("Creating Temp Collection {collName}, GUID: {collId}", collName, collId);
                if (penumbraEc != PenumbraApiEc.Success)
                {
                    logger.LogError("Failed to create temporary collection");
                }

                return collId;
            }

            var weaveId = _weaveCreateTemporaryCollection.InvokeFunc(collName);
            logger.LogTrace("Creating Weave Temp Collection {collName}, GUID: {collId}", collName, weaveId);
            return weaveId;
        }).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, HashSet<string>>?> GetCharacterData(ILogger logger, GameObjectHandler handler)
    {
        if (!APIAvailable)
        {
            return null;
        }

        return await Service.UseFramework(() =>
        {
            logger.LogTrace("Calling resource path IPC via {backend}", _backend);
            var idx = handler.GetGameObject()?.ObjectIndex;
            if (idx == null)
            {
                return null;
            }

            return _backend == ModBackend.Penumbra
                ? _penumbraResourcePaths.Invoke(idx.Value)[0]
                : _weaveResourcePaths.InvokeFunc(idx.Value)[0];
        }).ConfigureAwait(false);
    }

    public string GetMetaManipulations()
    {
        if (!APIAvailable)
        {
            return string.Empty;
        }

        return _backend == ModBackend.Penumbra
            ? _penumbraGetMetaManipulations.Invoke()
            : _weaveGetMetaManipulations.InvokeFunc();
    }

    public async Task RedrawAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if (!APIAvailable || _dalamudUtil.IsZoning)
        {
            return;
        }

        try
        {
            await _redrawManager.RedrawSemaphore.WaitAsync(token).ConfigureAwait(false);
            await _redrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, chara =>
            {
                logger.LogDebug("[{appid}] Calling redraw on {backend}", applicationId, _backend);
                InvokeRedraw(chara.ObjectIndex, RedrawType.Redraw);
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _redrawManager.RedrawSemaphore.Release();
        }
    }

    public async Task RemoveTemporaryCollectionAsync(ILogger logger, Guid applicationId, Guid collId)
    {
        if (!APIAvailable)
        {
            return;
        }

        await Service.UseFramework(() =>
        {
            logger.LogTrace("[{applicationId}] Removing temp collection for {collId}", applicationId, collId);
            if (_backend == ModBackend.Penumbra)
            {
                var ret = _penumbraRemoveTemporaryCollection.Invoke(collId);
                logger.LogTrace("[{applicationId}] RemoveTemporaryCollection: {ret}", applicationId, ret);
            }
            else
            {
                var ret = _weaveDeleteTemporaryCollection.InvokeFunc(collId);
                logger.LogTrace("[{applicationId}] Weave RemoveTemporaryCollection: {ret}", applicationId, ret);
            }
        }).ConfigureAwait(false);
    }

    public async Task<(string[] forward, string[][] reverse)> ResolvePathsAsync(string[] forward, string[] reverse)
        => _backend == ModBackend.Penumbra
            ? await _penumbraResolvePaths.Invoke(forward, reverse).ConfigureAwait(false)
            : _weaveResolvePaths.InvokeFunc(forward, reverse);

    public async Task SetManipulationDataAsync(ILogger logger, Guid applicationId, Guid collId, string manipulationData)
    {
        if (!APIAvailable)
        {
            return;
        }

        await Service.UseFramework(() =>
        {
            logger.LogTrace("[{applicationId}] Manip: {data}", applicationId, manipulationData);
            if (_backend == ModBackend.Penumbra)
            {
                var retAdd = _penumbraAddTemporaryMod.Invoke("SnowChara_Meta", collId, [], manipulationData, 0);
                logger.LogTrace("[{applicationId}] Setting temp meta mod for {collId}, Success: {ret}", applicationId, collId, retAdd);
            }
            else
            {
                var retAdd = _weaveAddTemporaryMod.InvokeFunc("SnowChara_Meta", collId, new Dictionary<string, string>(StringComparer.Ordinal), manipulationData, 0);
                logger.LogTrace("[{applicationId}] Setting Weave temp meta mod for {collId}, Success: {ret}", applicationId, collId, retAdd);
            }
        }).ConfigureAwait(false);
    }

    public async Task SetTemporaryModsAsync(ILogger logger, Guid applicationId, Guid collId, Dictionary<string, string> modPaths)
    {
        if (!APIAvailable)
        {
            return;
        }

        await Service.UseFramework(() =>
        {
            foreach (var mod in modPaths)
            {
                logger.LogTrace("[{applicationId}] Change: {from} => {to}", applicationId, mod.Key, mod.Value);
            }

            if (_backend == ModBackend.Penumbra)
            {
                var retRemove = _penumbraRemoveTemporaryMod.Invoke("SnowChara_Files", collId, 0);
                logger.LogTrace("[{applicationId}] Removing temp files mod for {collId}, Success: {ret}", applicationId, collId, retRemove);
                var retAdd = _penumbraAddTemporaryMod.Invoke("SnowChara_Files", collId, modPaths, string.Empty, 0);
                logger.LogTrace("[{applicationId}] Setting temp files mod for {collId}, Success: {ret}", applicationId, collId, retAdd);
            }
            else
            {
                var retRemove = _weaveRemoveTemporaryMod.InvokeFunc("SnowChara_Files", collId);
                logger.LogTrace("[{applicationId}] Removing Weave temp files mod for {collId}, Success: {ret}", applicationId, collId, retRemove);
                var retAdd = _weaveAddTemporaryMod.InvokeFunc("SnowChara_Files", collId, modPaths, string.Empty, 0);
                logger.LogTrace("[{applicationId}] Setting Weave temp files mod for {collId}, Success: {ret}", applicationId, collId, retAdd);
            }
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
        else if (_backend == ModBackend.Weave)
        {
            _weaveRedraw.InvokeFunc(objectIndex.Value, (int)setting);
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
            APIAvailable = true;
            ModDirectory = _penumbraResolveModDir.Invoke();
            _snowMediator.Publish(new PenumbraInitializedMessage());
            InvokeRedraw(0, RedrawType.Redraw);
        }
    }

    private void OnWeaveInit()
    {
        CheckAPI();
        if (_backend == ModBackend.Weave)
        {
            CheckModDirectory();
            _snowMediator.Publish(new PenumbraInitializedMessage());
            InvokeRedraw(0, RedrawType.Redraw);
        }
    }

    private void OnWeaveDispose()
    {
        if (_backend == ModBackend.Weave)
        {
            _redrawManager.Cancel();
            _snowMediator.Publish(new PenumbraDisposedMessage());
        }
    }

    private void OnWeaveResourceLoaded(nint ptr, string gamePath, string resolvedPath)
    {
        if (_backend == ModBackend.Weave)
        {
            ResourceLoaded(ptr, gamePath, resolvedPath);
        }
    }

    private void OnWeaveModSettingChanged()
    {
        if (_backend == ModBackend.Weave)
        {
            _snowMediator.Publish(new PenumbraModSettingChangedMessage());
        }
    }

    private void OnWeaveRedrawEvent(nint address, int objectIndex)
    {
        if (_backend == ModBackend.Weave)
        {
            RedrawEvent(address, objectIndex);
        }
    }
}
