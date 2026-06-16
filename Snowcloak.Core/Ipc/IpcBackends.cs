using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Handlers;

namespace Snowcloak.Interop.Ipc;

public interface IIpcCaller : IDisposable
{
    IpcStatus Status { get; }
    bool APIAvailable { get; }
    void CheckAPI();
}

public interface IPenumbraApply
{
    Task<Guid> CreateTemporaryCollectionAsync(ILogger logger, string uid);

    Task AssignTemporaryCollectionAsync(ILogger logger, Guid collName, int idx);

    Task SetTemporaryModsAsync(ILogger logger, Guid applicationId, Guid collId, Dictionary<string, string> modPaths);

    Task SetManipulationDataAsync(ILogger logger, Guid applicationId, Guid collId, string manipulationData);

    Task RedrawAsync(ILogger logger, IGameObjectHandle handler, Guid applicationId, CancellationToken token);
}

public interface IGlamourerIpc : IIpcCaller
{
    Task ApplyAllAsync(ILogger logger, IGameObjectHandle handler, string? customization, Guid applicationId, CancellationToken token, bool fireAndForget = false);

    Task<string> GetCharacterCustomizationAsync(IntPtr character);

    Task RevertAsync(ILogger logger, IGameObjectHandle handler, Guid applicationId, CancellationToken token);

    Task RevertByNameAsync(ILogger logger, string name, Guid applicationId);
}

public interface ICustomizePlusIpc : IIpcCaller
{
    Task RevertAsync(nint character);

    Task<Guid?> SetBodyScaleAsync(nint character, string scale);

    Task RevertByIdAsync(Guid? profileId);

    Task<string?> GetScaleAsync(nint character);
}

public interface IHeelsIpc : IIpcCaller
{
    Task<string> GetOffsetAsync();

    Task RestoreOffsetForPlayerAsync(IntPtr character);

    Task SetOffsetForPlayerAsync(IntPtr character, string data);
}

public interface IHonorificIpc : IIpcCaller
{
    Task ClearTitleAsync(nint character);

    Task<string> GetTitle();

    Task SetTitleAsync(IntPtr character, string honorificDataB64);
}

public interface IMoodlesIpc : IIpcCaller
{
    Task<string?> GetStatusAsync(nint address);

    Task SetStatusAsync(nint pointer, string status);

    Task RevertStatusAsync(nint pointer);
}

public interface IPetNamesIpc : IIpcCaller
{
    string GetLocalNames();

    Task SetPlayerData(nint character, string playerData);

    Task ClearPlayerData(nint characterPointer);
}
