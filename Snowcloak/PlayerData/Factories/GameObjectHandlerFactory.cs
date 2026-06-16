using Snowcloak.API.Data.Enum;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;

using ElezenTools.Services;

namespace Snowcloak.PlayerData.Factories;

public class GameObjectHandlerFactory
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly GameObjectHandlerMonitor _monitor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SnowMediator _snowMediator;

    public GameObjectHandlerFactory(ILoggerFactory loggerFactory, SnowMediator snowMediator,
        DalamudUtilService dalamudUtilService, GameObjectHandlerMonitor monitor)
    {
        _loggerFactory = loggerFactory;
        _snowMediator = snowMediator;
        _dalamudUtilService = dalamudUtilService;
        _monitor = monitor;
    }

    public async Task<GameObjectHandler> Create(ObjectKind objectKind, Func<nint> getAddressFunc, bool isWatched = false)
    {
        return await Service.RunOnFrameworkAsync(() => new GameObjectHandler(_loggerFactory.CreateLogger<GameObjectHandler>(),
            _snowMediator, _dalamudUtilService, _monitor, objectKind, getAddressFunc, isWatched)).ConfigureAwait(false);
    }
}
