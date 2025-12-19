using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Group;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Localisation;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI;
using Snowcloak.WebAPI;

namespace Snowcloak.Services;

public class UiFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SnowMediator _snowMediator;
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly SnowProfileManager _snowProfileManager;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private readonly LocalisationService _localisationService;

    public UiFactory(ILoggerFactory loggerFactory, SnowMediator snowMediator, ApiController apiController,
        UiSharedService uiSharedService, PairManager pairManager, ServerConfigurationManager serverConfigManager,
        SnowProfileManager snowProfileManager, PerformanceCollectorService performanceCollectorService, LocalisationService localisationService)
    {
        _loggerFactory = loggerFactory;
        _snowMediator = snowMediator;
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _pairManager = pairManager;
        _serverConfigManager = serverConfigManager;
        _snowProfileManager = snowProfileManager;
        _performanceCollectorService = performanceCollectorService;
        _localisationService = localisationService;
    }

    public SyncshellAdminUI CreateSyncshellAdminUi(GroupFullInfoDto dto)
    {
        return new SyncshellAdminUI(_loggerFactory.CreateLogger<SyncshellAdminUI>(), _snowMediator,
            _apiController, _uiSharedService, _pairManager, dto, _performanceCollectorService, _localisationService);
    }

    public StandaloneProfileUi CreateStandaloneProfileUi(UserData userData, Pair? pair = null, ProfileVisibility? requestedVisibility = null)
    {
        pair ??= _pairManager.GetPairByUID(userData.UID);
        return new StandaloneProfileUi(_loggerFactory.CreateLogger<StandaloneProfileUi>(), _snowMediator,
            _uiSharedService, _serverConfigManager, _snowProfileManager, _pairManager, pair, userData, requestedVisibility, _performanceCollectorService, _localisationService);
    }

    public PermissionWindowUI CreatePermissionPopupUi(Pair pair)
    {
        return new PermissionWindowUI(_loggerFactory.CreateLogger<PermissionWindowUI>(), pair,
            _snowMediator, _uiSharedService, _apiController, _performanceCollectorService, _localisationService);
    }

    public PlayerAnalysisUI CreatePlayerAnalysisUi(Pair pair)
    {
        return new PlayerAnalysisUI(_loggerFactory.CreateLogger<PlayerAnalysisUI>(), pair,
            _snowMediator, _uiSharedService, _performanceCollectorService, _localisationService);
    }
}
