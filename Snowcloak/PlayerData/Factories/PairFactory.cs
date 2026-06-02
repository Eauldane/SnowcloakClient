using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Services.ModNullification;

namespace Snowcloak.PlayerData.Factories;

public class PairFactory
{
    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SnowMediator _snowMediator;
    private readonly SnowcloakConfigService _snowcloakConfig;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly ModNullificationService _modNullificationService;

    public PairFactory(ILoggerFactory loggerFactory, PairHandlerFactory cachedPlayerFactory,
        SnowMediator snowMediator, SnowcloakConfigService snowcloakConfig, ServerConfigurationManager serverConfigurationManager,
        ModNullificationService modNullificationService)
    {
        _loggerFactory = loggerFactory;
        _cachedPlayerFactory = cachedPlayerFactory;
        _snowMediator = snowMediator;
        _snowcloakConfig = snowcloakConfig;
        _serverConfigurationManager = serverConfigurationManager;
        _modNullificationService = modNullificationService;
    }

    public Pair Create(UserData userData)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), userData, _cachedPlayerFactory, _snowMediator, _snowcloakConfig,
            _serverConfigurationManager, _modNullificationService);
    }
}
