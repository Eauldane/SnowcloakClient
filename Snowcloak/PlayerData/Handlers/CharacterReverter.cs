using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using ElezenTools.Core.Async;
using Snowcloak.Interop.Ipc;
using Snowcloak.PlayerData.Factories;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Utils;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Handlers;

internal sealed class CharacterReverter
{
    private readonly PairHandler _handler;
    private readonly ILogger Logger;
    private readonly Pair Pair;
    private readonly PairAppliedState _appliedState;
    private readonly IpcManager _ipcManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly CancellationTokenSource _runtimeCts;
    private readonly SingleFlightCts _applicationFlight;
    private readonly SingleFlightCts _downloadFlight;

    public CharacterReverter(PairHandler handler, ILogger logger, Pair pair, PairAppliedState appliedState,
        IpcManager ipcManager, DalamudUtilService dalamudUtil, GameObjectHandlerFactory gameObjectHandlerFactory,
        BackgroundTaskTracker backgroundTasks, CancellationTokenSource runtimeCts,
        SingleFlightCts applicationFlight, SingleFlightCts downloadFlight)
    {
        _handler = handler;
        Logger = logger;
        Pair = pair;
        _appliedState = appliedState;
        _ipcManager = ipcManager;
        _dalamudUtil = dalamudUtil;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _backgroundTasks = backgroundTasks;
        _runtimeCts = runtimeCts;
        _applicationFlight = applicationFlight;
        _downloadFlight = downloadFlight;
    }

    public nint GetPlayerScopedOptionalCleanupAddress()
    {
        var address = _handler.CharaHandler?.Address ?? nint.Zero;
        if (address != nint.Zero) return address;
        if (_appliedState.LastPlayerScopedOptionalAddress != nint.Zero) return _appliedState.LastPlayerScopedOptionalAddress;
        return _appliedState.LastKnownPlayerAddress;
    }

    public bool HasPlayerScopedOptionalDataToClear()
    {
        var cachedCustomize = _appliedState.CachedData?.CustomizePlusData.TryGetValue(ObjectKind.Player, out var customizePlusData) == true
            ? customizePlusData
            : string.Empty;

        return _appliedState.HasPlayerScopedOptionalDataApplied
            || _appliedState.CustomizeIds.ContainsKey(ObjectKind.Player)
            || !string.IsNullOrEmpty(cachedCustomize)
            || !string.IsNullOrEmpty(_appliedState.CachedData?.HeelsData)
            || !string.IsNullOrEmpty(_appliedState.CachedData?.HonorificData)
            || !string.IsNullOrEmpty(_appliedState.CachedData?.MoodlesData)
            || !string.IsNullOrEmpty(_appliedState.CachedData?.PetNamesData);
    }

    public void QueueClearPlayerScopedOptionalData(Guid applicationId)
    {
        var address = GetPlayerScopedOptionalCleanupAddress();
        if (address == nint.Zero || !HasPlayerScopedOptionalDataToClear())
        {
            return;
        }

        _ = _backgroundTasks.Run(async ct =>
        {
            using var scope = Logger.BeginScope("{ApplicationId}", applicationId);
            try
            {
                await ClearPlayerScopedOptionalDataAsync(address, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to clear player-scoped optional data for {alias}", Pair.UserData.AliasOrUID);
            }
        }, nameof(QueueClearPlayerScopedOptionalData), _runtimeCts.Token);
    }

    public async Task ClearPlayerScopedOptionalDataAsync(nint address, CancellationToken token)
    {
        if (address == nint.Zero || !HasPlayerScopedOptionalDataToClear())
        {
            return;
        }

        Logger.LogDebug("Clearing player-scoped optional data for {alias}/{name}", Pair.UserData.AliasOrUID, _handler.PlayerName);

        token.ThrowIfCancellationRequested();
        if (_appliedState.CustomizeIds.TryGetValue(ObjectKind.Player, out var customizeId))
        {
            _appliedState.CustomizeIds.Remove(ObjectKind.Player);
            if (customizeId != null)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
            }
            else
            {
                await _ipcManager.CustomizePlus.RevertAsync(address).ConfigureAwait(false);
            }
        }
        else if (_appliedState.CachedData?.CustomizePlusData.TryGetValue(ObjectKind.Player, out var cachedCustomize) == true
            && !string.IsNullOrEmpty(cachedCustomize))
        {
            await _ipcManager.CustomizePlus.RevertAsync(address).ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
        await _ipcManager.Heels.RestoreOffsetForPlayerAsync(address).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await _ipcManager.Honorific.ClearTitleAsync(address).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await _ipcManager.PetNames.ClearPlayerData(address).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await _ipcManager.Moodles.RevertStatusAsync(address).ConfigureAwait(false);

        _appliedState.HasPlayerScopedOptionalDataApplied = false;
        _appliedState.LastPlayerScopedOptionalAddress = nint.Zero;
    }

    public void UndoApplication(Guid applicationId = default)
    {
        _ = _backgroundTasks.Run(async () =>
        {
            await UndoApplicationAsync(applicationId).ConfigureAwait(false);
        }, nameof(UndoApplication));
    }

    public async Task UndoApplicationAsync(Guid applicationId = default)
    {
        Logger.LogDebug($"Undoing application of {Pair.UserPair}");
        var name = _handler.PlayerName;
        var optionalCleanupAddress = GetPlayerScopedOptionalCleanupAddress();
        try
        {
            if (applicationId == default)
                applicationId = Guid.NewGuid();
            using var scope = Logger.BeginScope("{ApplicationId}", applicationId);
            _applicationFlight.Cancel();
            _downloadFlight.Cancel();

            Logger.LogDebug("Removing Temp Collection for {name} ({user})", name, Pair.UserPair);
            if (_handler.PenumbraCollection != Guid.Empty)
            {
                await _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(Logger, applicationId, _handler.PenumbraCollection).ConfigureAwait(false);
                _handler.PenumbraCollection = Guid.Empty;
            }

            if (_dalamudUtil is { IsZoning: false, IsInCutscene: false } && !string.IsNullOrEmpty(name))
            {
                Logger.LogTrace("Restoring state for {name} ({OnlineUser})", name, Pair.UserPair);
                if (!_handler.IsVisible)
                {
                    Logger.LogDebug("Restoring Glamourer for {name} ({user})", name, Pair.UserPair);
                    await _ipcManager.Glamourer.RevertByNameAsync(Logger, name, applicationId).ConfigureAwait(false);
                }
                else
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(TimeSpan.FromSeconds(60));

                    Logger.LogInformation("CachedData is null {isNull}, contains things: {contains}", _appliedState.CachedData == null, _appliedState.CachedData?.FileReplacements.Any() ?? false);

                    foreach (KeyValuePair<ObjectKind, List<FileReplacementData>> item in _appliedState.CachedData?.FileReplacements ?? [])
                    {
                        try
                        {
                            await RevertCustomizationDataAsync(item.Key, name, applicationId, cts.Token).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            Logger.LogWarning(ex, "Failed disposing player (not present anymore?)");
                            break;
                        }
                    }
                }
            }

            await ClearPlayerScopedOptionalDataAsync(optionalCleanupAddress, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error on undoing application of {name}", name);
        }
    }

    private async Task RevertCustomizationDataAsync(ObjectKind objectKind, string name, Guid applicationId, CancellationToken cancelToken)
    {
        nint address = _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident);
        if (address == nint.Zero) return;

        Logger.LogDebug("Reverting all Customization for {alias}/{name} {objectKind}", Pair.UserData.AliasOrUID, name, objectKind);

        if (_appliedState.CustomizeIds.TryGetValue(objectKind, out var customizeId))
        {
            _appliedState.CustomizeIds.Remove(objectKind);
        }

        if (objectKind == ObjectKind.Player)
        {
            using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => address, isWatched: false).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("Restoring Customization and Equipment for {alias}/{name}", Pair.UserData.AliasOrUID, name);
            await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("Restoring Heels for {alias}/{name}", Pair.UserData.AliasOrUID, name);
            await _ipcManager.Heels.RestoreOffsetForPlayerAsync(address).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("Restoring C+ for {alias}/{name}", Pair.UserData.AliasOrUID, name);
            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("Restoring Honorific for {alias}/{name}", Pair.UserData.AliasOrUID, name);
            await _ipcManager.Honorific.ClearTitleAsync(address).ConfigureAwait(false);
            Logger.LogDebug("Restoring Pet Nicknames for {alias}/{name}", Pair.UserData.AliasOrUID, name);
            await _ipcManager.PetNames.ClearPlayerData(address).ConfigureAwait(false);
            Logger.LogDebug("Restoring Moodles for {alias}/{name}", Pair.UserData.AliasOrUID, name);
            await _ipcManager.Moodles.RevertStatusAsync(address).ConfigureAwait(false);
            _appliedState.HasPlayerScopedOptionalDataApplied = false;
            _appliedState.LastPlayerScopedOptionalAddress = nint.Zero;
        }
        else if (objectKind == ObjectKind.MinionOrMount)
        {
            var minionOrMount = await _dalamudUtil.GetMinionOrMountAsync(address).ConfigureAwait(false);
            if (minionOrMount != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => minionOrMount, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Pet)
        {
            var pet = await _dalamudUtil.GetPetAsync(address).ConfigureAwait(false);
            if (pet != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => pet, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Companion)
        {
            var companion = await _dalamudUtil.GetCompanionAsync(address).ConfigureAwait(false);
            if (companion != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Companion, () => companion, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
    }
}
