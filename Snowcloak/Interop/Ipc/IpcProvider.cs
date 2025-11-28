using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.SnowcloakConfiguration;
using Snowcloak.Utils;

namespace Snowcloak.Interop.Ipc;

public class IpcProvider : IHostedService, IMediatorSubscriber
{
    private readonly ILogger<IpcProvider> _logger;
    private readonly IDalamudPluginInterface _pi;
    private readonly CharaDataManager _charaDataManager;
    private ICallGateProvider<string, IGameObject, bool>? _loadFileProvider;
    private ICallGateProvider<string, IGameObject, Task<bool>>? _loadFileAsyncProvider;
    private ICallGateProvider<List<nint>>? _handledGameAddresses;
    private readonly List<GameObjectHandler> _activeGameObjectHandlers = [];

    public SnowMediator Mediator { get; init; }

    public IpcProvider(ILogger<IpcProvider> logger, IDalamudPluginInterface pi,
        CharaDataManager charaDataManager, SnowMediator snowMediator)
    {
        _logger = logger;
        _pi = pi;
        _charaDataManager = charaDataManager;
        Mediator = snowMediator;

        Mediator.Subscribe<GameObjectHandlerCreatedMessage>(this, (msg) =>
        {
            if (msg.OwnedObject) return;
            _activeGameObjectHandlers.Add(msg.GameObjectHandler);
        });
        Mediator.Subscribe<GameObjectHandlerDestroyedMessage>(this, (msg) =>
        {
            if (msg.OwnedObject) return;
            _activeGameObjectHandlers.Remove(msg.GameObjectHandler);
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting IpcProviderService");
        _loadFileProvider = _pi.GetIpcProvider<string, IGameObject, bool>("Snowcloak.LoadMcdf");
        _loadFileProvider.RegisterFunc(LoadMcdf);
        _loadFileAsyncProvider = _pi.GetIpcProvider<string, IGameObject, Task<bool>>("Snowcloak.LoadMcdfAsync");
        _loadFileAsyncProvider.RegisterFunc(LoadMcdfAsync);
        _handledGameAddresses = _pi.GetIpcProvider<List<nint>>("Snowcloak.GetHandledAddresses");
        _handledGameAddresses.RegisterFunc(GetHandledAddresses);
        _logger.LogInformation("Started IpcProviderService");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Stopping IpcProvider Service");
        _loadFileProvider?.UnregisterFunc();
        _loadFileAsyncProvider?.UnregisterFunc();
        _handledGameAddresses?.UnregisterFunc();
        Mediator.UnsubscribeAll(this);
        return Task.CompletedTask;
    }

    private async Task<bool> LoadMcdfAsync(string path, IGameObject target)
    {
        await ApplyFileAsync(path, target).ConfigureAwait(false);

        return true;
    }

    private bool LoadMcdf(string path, IGameObject target)
    {
        _ = Task.Run(async () => await ApplyFileAsync(path, target).ConfigureAwait(false)).ConfigureAwait(false);

        return true;
    }

    private async Task ApplyFileAsync(string path, IGameObject target)
    {
        _charaDataManager.LoadMcdf(path);
        await (_charaDataManager.LoadedMcdfHeader ?? Task.CompletedTask).ConfigureAwait(false);
        _charaDataManager.McdfApplyToTarget(target.Name.TextValue);
    }

    private List<nint> GetHandledAddresses()
    {
        return _activeGameObjectHandlers.Where(g => g.Address != nint.Zero).Select(g => g.Address).Distinct().ToList();
    }
}
