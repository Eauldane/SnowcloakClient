#if DEBUG
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Snowcloak.Core.Replay;
using Snowcloak.PlayerData.Handlers;

namespace Snowcloak.Interop.Ipc;

internal sealed class RecordingPenumbraIpc(IPenumbraIpc inner, IpcTraceRecorder recorder) : IPenumbraIpc
{
    public IpcStatus Status => inner.Status;
    public bool APIAvailable => inner.APIAvailable;
    public string? ModDirectory => inner.ModDirectory;
    public void CheckAPI() => inner.CheckAPI();
    public void CheckModDirectory() => inner.CheckModDirectory();
    public void Dispose() { }

    public async Task<Guid> CreateTemporaryCollectionAsync(ILogger logger, string uid)
    {
        var coll = await inner.CreateTemporaryCollectionAsync(logger, uid).ConfigureAwait(false);
        if (recorder.IsCapturing)
        {
            recorder.Record(new PenumbraCreateTemporaryCollection(recorder.Collection(coll)));
        }

        return coll;
    }

    public async Task AssignTemporaryCollectionAsync(ILogger logger, Guid collName, int idx)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new PenumbraAssignTemporaryCollection(recorder.Collection(collName), recorder.HandleIndex(idx)));
        }

        await inner.AssignTemporaryCollectionAsync(logger, collName, idx).ConfigureAwait(false);
    }

    public async Task SetTemporaryModsAsync(ILogger logger, Guid applicationId, Guid collId, Dictionary<string, string> modPaths)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(PenumbraSetTemporaryMods.Create(recorder.Application(applicationId), recorder.Collection(collId), modPaths));
        }

        await inner.SetTemporaryModsAsync(logger, applicationId, collId, modPaths).ConfigureAwait(false);
    }

    public async Task SetManipulationDataAsync(ILogger logger, Guid applicationId, Guid collId, string manipulationData)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new PenumbraSetManipulationData(recorder.Application(applicationId), recorder.Collection(collId), manipulationData));
        }

        await inner.SetManipulationDataAsync(logger, applicationId, collId, manipulationData).ConfigureAwait(false);
    }

    public async Task RemoveTemporaryCollectionAsync(ILogger logger, Guid applicationId, Guid collId)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new PenumbraRemoveTemporaryCollection(recorder.Application(applicationId), recorder.Collection(collId)));
        }

        await inner.RemoveTemporaryCollectionAsync(logger, applicationId, collId).ConfigureAwait(false);
    }

    public async Task RedrawAsync(ILogger logger, IGameObjectHandle handler, Guid applicationId, CancellationToken token)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new PenumbraRedraw(recorder.Application(applicationId), recorder.Handle(handler.Address)));
        }

        await inner.RedrawAsync(logger, handler, applicationId, token).ConfigureAwait(false);
    }

    public Task ConvertTextureFiles(ILogger logger, Dictionary<string, (TextureType TextureType, string[] Duplicates)> textures, IProgress<(string, int)> progress, CancellationToken token)
        => inner.ConvertTextureFiles(logger, textures, progress, token);

    public Task<Dictionary<string, HashSet<string>>?> GetCharacterData(ILogger logger, IGameObjectHandle handler)
        => inner.GetCharacterData(logger, handler);

    public string GetMetaManipulations() => inner.GetMetaManipulations();

    public Task<(string[] forward, string[][] reverse)> ResolvePathsAsync(string[] forward, string[] reverse)
        => inner.ResolvePathsAsync(forward, reverse);
}

internal sealed class RecordingGlamourerIpc(IGlamourerIpc inner, IpcTraceRecorder recorder) : IGlamourerIpc
{
    public IpcStatus Status => inner.Status;
    public bool APIAvailable => inner.APIAvailable;
    public void CheckAPI() => inner.CheckAPI();
    public void Dispose() { }

    public async Task ApplyAllAsync(ILogger logger, IGameObjectHandle handler, string? customization, Guid applicationId, CancellationToken token, bool fireAndForget = false)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new GlamourerApplyAll(recorder.Application(applicationId), recorder.Handle(handler.Address), customization ?? string.Empty));
        }

        await inner.ApplyAllAsync(logger, handler, customization, applicationId, token, fireAndForget).ConfigureAwait(false);
    }

    public async Task RevertAsync(ILogger logger, IGameObjectHandle handler, Guid applicationId, CancellationToken token)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new GlamourerRevert(recorder.Application(applicationId), recorder.Handle(handler.Address)));
        }

        await inner.RevertAsync(logger, handler, applicationId, token).ConfigureAwait(false);
    }

    public async Task RevertByNameAsync(ILogger logger, string name, Guid applicationId)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new GlamourerRevertByName(recorder.Application(applicationId), name));
        }

        await inner.RevertByNameAsync(logger, name, applicationId).ConfigureAwait(false);
    }

    public Task<string> GetCharacterCustomizationAsync(IntPtr character) => inner.GetCharacterCustomizationAsync(character);
}

internal sealed class RecordingCustomizePlusIpc(ICustomizePlusIpc inner, IpcTraceRecorder recorder) : ICustomizePlusIpc
{
    public IpcStatus Status => inner.Status;
    public bool APIAvailable => inner.APIAvailable;
    public void CheckAPI() => inner.CheckAPI();
    public void Dispose() { }

    public async Task<Guid?> SetBodyScaleAsync(nint character, string scale)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new CustomizeSetBodyScale(recorder.Handle(character), scale));
        }

        return await inner.SetBodyScaleAsync(character, scale).ConfigureAwait(false);
    }

    public async Task RevertByIdAsync(Guid? profileId)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new CustomizeRevertById(recorder.CustomizeId(profileId ?? Guid.Empty)));
        }

        await inner.RevertByIdAsync(profileId).ConfigureAwait(false);
    }

    public Task RevertAsync(nint character) => inner.RevertAsync(character);

    public Task<string?> GetScaleAsync(nint character) => inner.GetScaleAsync(character);
}

internal sealed class RecordingHeelsIpc(IHeelsIpc inner, IpcTraceRecorder recorder) : IHeelsIpc
{
    public IpcStatus Status => inner.Status;
    public bool APIAvailable => inner.APIAvailable;
    public void CheckAPI() => inner.CheckAPI();
    public void Dispose() { }

    public async Task SetOffsetForPlayerAsync(IntPtr character, string data)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new HeelsSetOffset(recorder.Handle(character), data));
        }

        await inner.SetOffsetForPlayerAsync(character, data).ConfigureAwait(false);
    }

    public Task<string> GetOffsetAsync() => inner.GetOffsetAsync();

    public Task RestoreOffsetForPlayerAsync(IntPtr character) => inner.RestoreOffsetForPlayerAsync(character);
}

internal sealed class RecordingHonorificIpc(IHonorificIpc inner, IpcTraceRecorder recorder) : IHonorificIpc
{
    public IpcStatus Status => inner.Status;
    public bool APIAvailable => inner.APIAvailable;
    public void CheckAPI() => inner.CheckAPI();
    public void Dispose() { }

    public async Task SetTitleAsync(IntPtr character, string honorificDataB64)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new HonorificSetTitle(recorder.Handle(character), honorificDataB64));
        }

        await inner.SetTitleAsync(character, honorificDataB64).ConfigureAwait(false);
    }

    public Task ClearTitleAsync(nint character) => inner.ClearTitleAsync(character);

    public Task<string> GetTitle() => inner.GetTitle();
}

internal sealed class RecordingMoodlesIpc(IMoodlesIpc inner, IpcTraceRecorder recorder) : IMoodlesIpc
{
    public IpcStatus Status => inner.Status;
    public bool APIAvailable => inner.APIAvailable;
    public void CheckAPI() => inner.CheckAPI();
    public void Dispose() { }

    public async Task SetStatusAsync(nint pointer, string status)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new MoodlesSetStatus(recorder.Handle(pointer), status));
        }

        await inner.SetStatusAsync(pointer, status).ConfigureAwait(false);
    }

    public Task<string?> GetStatusAsync(nint address) => inner.GetStatusAsync(address);

    public Task RevertStatusAsync(nint pointer) => inner.RevertStatusAsync(pointer);
}

internal sealed class RecordingPetNamesIpc(IPetNamesIpc inner, IpcTraceRecorder recorder) : IPetNamesIpc
{
    public IpcStatus Status => inner.Status;
    public bool APIAvailable => inner.APIAvailable;
    public void CheckAPI() => inner.CheckAPI();
    public void Dispose() { }

    public async Task SetPlayerData(nint character, string playerData)
    {
        if (recorder.IsCapturing)
        {
            recorder.Record(new PetNamesSetPlayerData(recorder.Handle(character), playerData));
        }

        await inner.SetPlayerData(character, playerData).ConfigureAwait(false);
    }

    public string GetLocalNames() => inner.GetLocalNames();

    public Task ClearPlayerData(nint characterPointer) => inner.ClearPlayerData(characterPointer);
}
#endif
