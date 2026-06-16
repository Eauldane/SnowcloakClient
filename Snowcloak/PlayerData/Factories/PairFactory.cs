using Snowcloak.API.Data;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;

namespace Snowcloak.PlayerData.Factories;

public class PairFactory
{
    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly BlockListStore _blockListStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NotesStore _notesStore;
    private readonly PairAppearanceCacheService _pairAppearanceCache;
    private readonly SnowMediator _snowMediator;
    private readonly SnowcloakConfigService _snowcloakConfig;

    public PairFactory(ILoggerFactory loggerFactory, PairHandlerFactory cachedPlayerFactory,
        SnowMediator snowMediator, SnowcloakConfigService snowcloakConfig, NotesStore notesStore, BlockListStore blockListStore,
        PairAppearanceCacheService pairAppearanceCache)
    {
        _loggerFactory = loggerFactory;
        _cachedPlayerFactory = cachedPlayerFactory;
        _snowMediator = snowMediator;
        _snowcloakConfig = snowcloakConfig;
        _notesStore = notesStore;
        _blockListStore = blockListStore;
        _pairAppearanceCache = pairAppearanceCache;
    }

    public Pair Create(UserData userData)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), userData, _cachedPlayerFactory, _snowMediator, _snowcloakConfig,
            _notesStore, _blockListStore, _pairAppearanceCache);
    }
}
