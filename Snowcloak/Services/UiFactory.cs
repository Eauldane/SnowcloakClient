using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Group;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Interop.Ipc;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;
using Snowcloak.UI.Components;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.Core.Chat;
using Snowcloak.Services.Chat;
using Dalamud.Interface.ImGuiFileDialog;

namespace Snowcloak.Services;

public class UiFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SnowMediator _snowMediator;
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IpcManager _ipcManager;
    private readonly BbCodeRenderService _bbCodeRenderService;
    private readonly TextureService _textureService;
    private readonly ImageTransferService _imageTransferService;
    private readonly UiFontService _fontService;
    private readonly PairManager _pairManager;
    private readonly PairRequestService _pairRequestService;
    private readonly SnowProfileManager _snowProfileManager;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private readonly SyncTroubleshootingService _syncTroubleshootingService;
    private readonly SyncshellBudgetService _syncshellBudgetService;
    private readonly ChatClientService _chatService;
    private readonly ChatIdentityResolver _chatIdentityResolver;
    private readonly ImGuiChatRenderer _chatRenderer;
    private readonly FileDialogManager _fileDialogManager;
    private readonly RoleplayClientService _roleplayClientService;

    public UiFactory(ILoggerFactory loggerFactory, SnowMediator snowMediator, ApiController apiController,
        SnowcloakConfigService configService,
        UiFontService fontService, BbCodeRenderService bbCodeRenderService, TextureService textureService, PairManager pairManager, PairRequestService pairRequestService,
        DalamudUtilService dalamudUtilService, IpcManager ipcManager,
        SnowProfileManager snowProfileManager, ImageTransferService imageTransferService, PerformanceCollectorService performanceCollectorService,
        SyncshellBudgetService syncshellBudgetService, SyncTroubleshootingService syncTroubleshootingService,
        ChatClientService chatService, ChatIdentityResolver chatIdentityResolver, ImGuiChatRenderer chatRenderer,
        FileDialogManager fileDialogManager, RoleplayClientService roleplayClientService)
    {
        _loggerFactory = loggerFactory;
        _snowMediator = snowMediator;
        _apiController = apiController;
        _configService = configService;
        _dalamudUtilService = dalamudUtilService;
        _ipcManager = ipcManager;
        _fontService = fontService;
        _bbCodeRenderService = bbCodeRenderService;
        _textureService = textureService;
        _imageTransferService = imageTransferService;
        _pairManager = pairManager;
        _pairRequestService = pairRequestService;
        _snowProfileManager = snowProfileManager;
        _performanceCollectorService = performanceCollectorService;
        _syncTroubleshootingService = syncTroubleshootingService;
        _syncshellBudgetService = syncshellBudgetService;
        _chatService = chatService;
        _chatIdentityResolver = chatIdentityResolver;
        _chatRenderer = chatRenderer;
        _fileDialogManager = fileDialogManager;
        _roleplayClientService = roleplayClientService;
    }

    public SyncshellAdminUI CreateSyncshellAdminUi(GroupFullInfoDto dto)
    {
        return new SyncshellAdminUI(_loggerFactory.CreateLogger<SyncshellAdminUI>(), _snowMediator,
            _apiController, _configService, _fontService, _pairManager, dto, _performanceCollectorService, _syncshellBudgetService,
            _dalamudUtilService, _fileDialogManager, _imageTransferService);
    }

    public SyncshellEventsWindow CreateSyncshellEventsUi(GroupFullInfoDto dto)
    {
        return new SyncshellEventsWindow(_loggerFactory.CreateLogger<SyncshellEventsWindow>(), _snowMediator,
            _apiController, _pairManager, _dalamudUtilService, _textureService, _imageTransferService, _fileDialogManager, dto,
            _performanceCollectorService);
    }

    public StandaloneProfileUi CreateStandaloneProfileUi(UserData userData, Pair? pair = null, ProfileVisibility? requestedVisibility = null,
        string? ident = null, string? fallbackName = null)
    {
        ArgumentNullException.ThrowIfNull(userData);

        if (pair == null && !string.IsNullOrWhiteSpace(userData.UID))
            pair = _pairManager.GetPairByUID(userData.UID);
        return new StandaloneProfileUi(_loggerFactory.CreateLogger<StandaloneProfileUi>(), _snowMediator,
            _fontService, _bbCodeRenderService, _textureService, _configService, _snowProfileManager, _imageTransferService, pair, userData, requestedVisibility, ident, fallbackName,
            _dalamudUtilService, _ipcManager, _performanceCollectorService);
    }

    public PermissionWindowUI CreatePermissionPopupUi(Pair pair)
    {
        return new PermissionWindowUI(_loggerFactory.CreateLogger<PermissionWindowUI>(), pair,
            _snowMediator, _fontService, _apiController, _performanceCollectorService);
    }

    public PlayerAnalysisUI CreatePlayerAnalysisUi(Pair pair)
    {
        return new PlayerAnalysisUI(_loggerFactory.CreateLogger<PlayerAnalysisUI>(), pair,
            _snowMediator, _performanceCollectorService);
    }

    public SyncTroubleshootingUi CreateSyncTroubleshootingUi(Pair pair)
    {
        return new SyncTroubleshootingUi(_loggerFactory.CreateLogger<SyncTroubleshootingUi>(), pair,
            _snowMediator, _syncTroubleshootingService, _performanceCollectorService);
    }

    public ChatPopoutWindow CreateChatPopoutWindow(ConversationKey key)
    {
        return new ChatPopoutWindow(_loggerFactory.CreateLogger<ChatPopoutWindow>(), _snowMediator,
            _chatService, _chatRenderer, _fileDialogManager, _performanceCollectorService, key);
    }

    public RoomAdministrationWindow CreateRoomAdministrationWindow(string roomId)
    {
        return new RoomAdministrationWindow(_loggerFactory.CreateLogger<RoomAdministrationWindow>(), _snowMediator,
            _apiController, _chatService, _pairManager, _chatIdentityResolver, _roleplayClientService,
            _fileDialogManager, _imageTransferService, _performanceCollectorService, roomId);
    }

    public PairRequestConfirmationWindow CreatePairRequestConfirmationWindow(string ident, string characterName, string pluginName)
    {
        return new PairRequestConfirmationWindow(_loggerFactory.CreateLogger<PairRequestConfirmationWindow>(), _snowMediator,
            _pairRequestService, _performanceCollectorService, ident, characterName, pluginName);
    }
}
