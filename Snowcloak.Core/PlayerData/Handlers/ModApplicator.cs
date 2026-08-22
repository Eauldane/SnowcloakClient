using Microsoft.Extensions.Logging;
using Snowcloak.Interop.Ipc;
using Snowcloak.PlayerData.Data;
using CharacterData = Snowcloak.API.Data.CharacterData;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Handlers;

public sealed partial class ModApplicator
{
    private readonly IPenumbraApply _penumbra;
    private readonly IGlamourerIpc _glamourer;
    private readonly ICustomizePlusIpc _customize;
    private readonly IHeelsIpc _heels;
    private readonly IHonorificIpc _honorific;
    private readonly IMoodlesIpc _moodles;
    private readonly IPetNamesIpc _petNames;
    private readonly IApplyGameState _gameState;

    public ModApplicator(IPenumbraApply penumbra, IGlamourerIpc glamourer, ICustomizePlusIpc customize,
        IHeelsIpc heels, IHonorificIpc honorific, IMoodlesIpc moodles, IPetNamesIpc petNames, IApplyGameState gameState)
    {
        _penumbra = penumbra;
        _glamourer = glamourer;
        _customize = customize;
        _heels = heels;
        _honorific = honorific;
        _moodles = moodles;
        _petNames = petNames;
        _gameState = gameState;
    }

    public async Task<bool> ApplyModsAsync(ILogger logger, IApplyTarget target, IGameObjectHandle handle, string collectionOwnerUid,
        Func<Task<ushort?>> resolveObjectIndex, Guid applicationId, bool updateModdedPaths, bool updateManip,
        Dictionary<string, string> moddedPaths, string manipulationData, CancellationToken token)
    {
        var objIndex = ushort.MaxValue;

        if (target.PenumbraCollection == Guid.Empty)
        {
            var index = await resolveObjectIndex().ConfigureAwait(false);
            if (index == null)
            {
                LogAbortingNoObjectIndex(logger);
                return false;
            }

            objIndex = index.Value;
            target.PenumbraCollection = await _penumbra.CreateTemporaryCollectionAsync(logger, collectionOwnerUid).ConfigureAwait(false);
            await _penumbra.AssignTemporaryCollectionAsync(logger, target.PenumbraCollection, objIndex).ConfigureAwait(false);
        }

        await _gameState.WaitWhileCharacterIsDrawing(logger, handle, applicationId, 30000, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        if (updateModdedPaths)
        {
            if (objIndex == ushort.MaxValue)
            {
                var index = await resolveObjectIndex().ConfigureAwait(false);
                if (index == null)
                {
                    LogAbortingNoObjectIndexBeforeMods(logger);
                    return false;
                }

                objIndex = index.Value;
            }

            await _penumbra.AssignTemporaryCollectionAsync(logger, target.PenumbraCollection, objIndex).ConfigureAwait(false);
            await _penumbra.SetTemporaryModsAsync(logger, applicationId, target.PenumbraCollection, moddedPaths).ConfigureAwait(false);
        }

        if (updateManip)
        {
            await _penumbra.SetManipulationDataAsync(logger, applicationId, target.PenumbraCollection, manipulationData).ConfigureAwait(false);
        }

        return true;
    }

    public static bool IsPlayerScopedOptionalChange(PlayerChanges change)
        => change is PlayerChanges.Customize
            or PlayerChanges.Heels
            or PlayerChanges.Honorific
            or PlayerChanges.Moodles
            or PlayerChanges.PetNames;

    public async Task ApplyKindAsync(ILogger logger, IGameObjectHandle handle, ObjectKind kind, IReadOnlyCollection<PlayerChanges> changes,
        CharacterData charaData, PairAppliedState appliedState, Guid applicationId, CancellationToken token)
    {
        if (kind == ObjectKind.Player && changes.Any(IsPlayerScopedOptionalChange))
        {
            appliedState.HasPlayerScopedOptionalDataApplied = true;
            appliedState.LastPlayerScopedOptionalAddress = handle.Address;
        }

        await _gameState.WaitWhileCharacterIsDrawing(logger, handle, applicationId, 30000, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        foreach (var change in CharacterDataChangeSet.GetOrderedChanges(changes))
        {
            LogProcessingChange(logger, change, kind);
            switch (change)
            {
                case PlayerChanges.Customize:
                    if (charaData.CustomizePlusData.TryGetValue(kind, out var customizePlusData))
                    {
                        appliedState.CustomizeIds[kind] = await _customize.SetBodyScaleAsync(handle.Address, customizePlusData).ConfigureAwait(false);
                    }
                    else if (appliedState.CustomizeIds.TryGetValue(kind, out var customizeId))
                    {
                        await _customize.RevertByIdAsync(customizeId).ConfigureAwait(false);
                        appliedState.CustomizeIds.Remove(kind);
                    }
                    break;

                case PlayerChanges.Heels:
                    await _heels.SetOffsetForPlayerAsync(handle.Address, charaData.HeelsData).ConfigureAwait(false);
                    break;

                case PlayerChanges.Honorific:
                    await _honorific.SetTitleAsync(handle.Address, charaData.HonorificData).ConfigureAwait(false);
                    break;

                case PlayerChanges.Glamourer:
                    if (charaData.GlamourerData.TryGetValue(kind, out var glamourerData))
                    {
                        await _glamourer.ApplyAllAsync(logger, handle, glamourerData, applicationId, token).ConfigureAwait(false);
                    }
                    break;

                case PlayerChanges.Moodles:
                    await _moodles.SetStatusAsync(handle.Address, charaData.MoodlesData).ConfigureAwait(false);
                    break;

                case PlayerChanges.PetNames:
                    await _petNames.SetPlayerData(handle.Address, charaData.PetNamesData).ConfigureAwait(false);
                    break;

                case PlayerChanges.ForcedRedraw:
                    await _penumbra.RedrawAsync(logger, handle, applicationId, token).ConfigureAwait(false);
                    break;

                default:
                    break;
            }
            token.ThrowIfCancellationRequested();
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing {change} for {kind}")]
    private static partial void LogProcessingChange(ILogger logger, PlayerChanges change, ObjectKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Aborting application task, unable to obtain object index")]
    private static partial void LogAbortingNoObjectIndex(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Aborting application task, unable to obtain object index before applying mods")]
    private static partial void LogAbortingNoObjectIndexBeforeMods(ILogger logger);
}
