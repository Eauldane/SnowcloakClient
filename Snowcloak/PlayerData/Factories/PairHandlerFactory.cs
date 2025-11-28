using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.SnowcloakConfiguration;

namespace Snowcloak.PlayerData.Factories;

public class PairHandlerFactory
{
    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileDownloadManagerFactory _fileDownloadManagerFactory;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly IpcManager _ipcManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SnowMediator _snowMediator;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager;
    private readonly PairAnalyzerFactory _pairAnalyzerFactory;
    private readonly VisibilityService _visibilityService;
    private readonly DatabaseService _databaseService;

    public PairHandlerFactory(ILoggerFactory loggerFactory, GameObjectHandlerFactory gameObjectHandlerFactory, IpcManager ipcManager,
        FileDownloadManagerFactory fileDownloadManagerFactory, DalamudUtilService dalamudUtilService,
        PluginWarningNotificationService pluginWarningNotificationManager, IHostApplicationLifetime hostApplicationLifetime,
        FileCacheManager fileCacheManager, SnowMediator snowMediator, PlayerPerformanceService playerPerformanceService,
        ServerConfigurationManager serverConfigManager, PairAnalyzerFactory pairAnalyzerFactory,
        SnowcloakConfigService configService, VisibilityService visibilityService, DatabaseService databaseService)
    {
        _loggerFactory = loggerFactory;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _ipcManager = ipcManager;
        _fileDownloadManagerFactory = fileDownloadManagerFactory;
        _dalamudUtilService = dalamudUtilService;
        _pluginWarningNotificationManager = pluginWarningNotificationManager;
        _hostApplicationLifetime = hostApplicationLifetime;
        _fileCacheManager = fileCacheManager;
        _snowMediator = snowMediator;
        _playerPerformanceService = playerPerformanceService;
        _serverConfigManager = serverConfigManager;
        _pairAnalyzerFactory = pairAnalyzerFactory;
        _configService = configService;
        _visibilityService = visibilityService;
        _databaseService = databaseService;
    }

    public PairHandler Create(Pair pair)
    {
        return new PairHandler(_loggerFactory.CreateLogger<PairHandler>(), pair, _pairAnalyzerFactory.Create(pair), _gameObjectHandlerFactory,
            _ipcManager, _fileDownloadManagerFactory.Create(), _pluginWarningNotificationManager, _dalamudUtilService, _hostApplicationLifetime,
            _fileCacheManager, _snowMediator, _playerPerformanceService, _serverConfigManager, _configService, _visibilityService, _databaseService);
    }
}