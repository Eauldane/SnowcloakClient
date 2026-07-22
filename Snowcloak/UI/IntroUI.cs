using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Utility;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Core.Onboarding;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.UI.Components.Account;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Numerics;

namespace Snowcloak.UI;

public partial class IntroUi : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly SnowcloakConfigService _configService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly ServerRegistry _serverConfigurationManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly AccountRegistrationService _registerService;
    private readonly ApiController _apiController;
    private readonly PluginAvailabilityPanel _pluginAvailabilityPanel;
    private readonly ServiceSelectionPanel _serviceSelectionPanel;
    private readonly StorageSettingsPanel _storageSettingsPanel;
    private readonly UiFontService _fontService;
    private readonly ImTextureID? _logoTextureHandle;
    private readonly PasswordAccountFlow _accountFlow = new();
    private readonly SecretKeyBackupFlow _secretKeyBackupFlow;
    private readonly StandaloneKeyRegistrationFlow _standaloneKeyFlow;

    private bool _readFirstPage;
    private ServiceRegistrationMode _serviceRegistrationMode;
    private string _secretKey = string.Empty;
    private string _timeoutLabel = string.Empty;
    private Task? _timeoutTask;

    private enum ServiceRegistrationMode
    {
        CreateKey,
        UseExistingKey,
        RestoreBackup,
        Account,
    }

    public IntroUi(ILogger<IntroUi> logger, UiFontService fontService, PluginAvailabilityPanel pluginAvailabilityPanel,
        StorageSettingsPanel storageSettingsPanel, ServiceSelectionPanel serviceSelectionPanel,
        TextureService textureService, IDalamudPluginInterface pluginInterface,
        FileDialogManager fileDialogManager, ApiController apiController, SnowcloakConfigService configService,
        CacheMonitor fileCacheManager, ServerRegistry serverConfigurationManager, SnowMediator snowMediator,
        PerformanceCollectorService performanceCollectorService, DalamudUtilService dalamudUtilService,
        AccountRegistrationService registerService, SecretKeyBackupService secretKeyBackupService)
        : base(logger, snowMediator, "Snowcloak Setup", performanceCollectorService)
    {
        _fontService = fontService;
        _pluginAvailabilityPanel = pluginAvailabilityPanel;
        _storageSettingsPanel = storageSettingsPanel;
        _serviceSelectionPanel = serviceSelectionPanel;
        ArgumentNullException.ThrowIfNull(textureService);
        ArgumentNullException.ThrowIfNull(pluginInterface);
        var pluginDirectory = pluginInterface.AssemblyLocation.Directory?.FullName ?? pluginInterface.ConfigDirectory.FullName;
        _logoTextureHandle = textureService.GetFile(Path.Combine(pluginDirectory, "images", "logo-sidebar.png"))?.Handle;
        _apiController = apiController;
        _configService = configService;
        _cacheMonitor = fileCacheManager;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;
        _registerService = registerService;
        _secretKeyBackupFlow = new SecretKeyBackupFlow(logger, secretKeyBackupService, fileDialogManager);
        _standaloneKeyFlow = new StandaloneKeyRegistrationFlow(logger);
        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;

        SetScaledSizeConstraints(new Vector2(820, 560), new Vector2(1100, 900));
        Size = new Vector2(900, 640);
        SizeCondition = ImGuiCond.FirstUseEver;

        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) =>
        {
            _configService.Current.UseCompactor = !dalamudUtilService.IsWine;
            IsOpen = true;
        });
    }

    protected override void DrawInternal()
    {
        if (_dalamudUtilService.IsInGpose) return;

        var step = OnboardingStateMachine.Resolve(new OnboardingInputs(
            AgreementAccepted: _configService.Current.AcceptedAgreement,
            RequirementsAcknowledged: _readFirstPage,
            StorageReady: IsStorageReady(),
            Connected: _apiController.IsConnected));

        if (step == OnboardingStep.Complete)
        {
            CompleteOnboarding();
            return;
        }

        SnowcloakUi.AccentColor = ElezenColours.SnowcloakBlue;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().WindowPadding.Y
            - ImGuiHelpers.GlobalScale + ImGui.GetStyle().ItemSpacing.Y);
        DrawSetupShell(step);
    }

    private bool IsStorageReady()
    {
        var cacheFolder = _configService.Current.CacheFolder;
        return !string.IsNullOrEmpty(cacheFolder)
            && _configService.Current.InitialScanComplete
            && Directory.Exists(cacheFolder);
    }

    private void CompleteOnboarding()
    {
        _secretKey = string.Empty;
        _serverConfigurationManager.Save();
        Mediator.Publish(new SwitchToMainUiMessage());
        IsOpen = false;
    }

    // ----- Command methods: render-triggered side effects are funnelled through these. -----

    private void BeginAgreementTimeout()
    {
        _readFirstPage = true;
#if !DEBUG
        _timeoutTask = Task.Run(async () =>
        {
            for (int i = 10; i > 0; i--)
            {
                _timeoutLabel = string.Format(CultureInfo.InvariantCulture, "'I agree' button will be available in {0}s", i);
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        });
#else
        _timeoutTask = Task.CompletedTask;
#endif
    }

    private void BeginSecretKeyBackupImport()
    {
        _secretKeyBackupFlow.BeginImportForInitialSetup(imported =>
        {
            _secretKey = string.Empty;
            _standaloneKeyFlow.Reset();
            if (imported.CurrentCharacterAssigned)
                _ = Task.Run(() => _apiController.CreateConnections());
        });
    }

    private void SaveAndConnectWithSecretKey()
    {
        if (_serverConfigurationManager.CurrentServer == null) _serverConfigurationManager.SelectServer(0);
        var server = _serverConfigurationManager.CurrentServer!;
        var normalisedKey = NormalizeSecretKey(_secretKey);
        var existingKey = server.SecretKeys.FirstOrDefault(item =>
            string.Equals(NormalizeSecretKey(item.Value.Key), normalisedKey, StringComparison.Ordinal));
        var keyIndex = existingKey.Value == null
            ? server.SecretKeys.Count == 0 ? 0 : server.SecretKeys.Keys.Max() + 1
            : existingKey.Key;
        var reply = _standaloneKeyFlow.Reply;
        if (existingKey.Value == null)
        {
            var keyName = reply != null && normalisedKey.Equals(NormalizeSecretKey(reply.SecretKey ?? string.Empty), StringComparison.Ordinal)
                ? reply.UID + " " + string.Format(CultureInfo.InvariantCulture, "(registered {0:yyyy-MM-dd})", DateTime.Now)
                : string.Format(CultureInfo.InvariantCulture, "Secret Key added on Setup ({0:yyyy-MM-dd})", DateTime.Now);
            server.SecretKeys.Add(keyIndex, new SecretKey
            {
                FriendlyName = keyName,
                Key = normalisedKey,
            });
        }

        var playerName = _dalamudUtilService.GetPlayerName();
        var playerWorldId = _dalamudUtilService.GetHomeWorldId();
        server.Authentications.RemoveAll(item =>
            string.Equals(item.CharacterName, playerName, StringComparison.OrdinalIgnoreCase)
            && item.WorldId == playerWorldId);
        _serverConfigurationManager.AddCurrentCharacterToServer(secretKeyIdx: keyIndex, save: false);
        _serverConfigurationManager.Save();
        _ = Task.Run(() => _apiController.CreateConnections());
    }

    // ----- Account credential delegates handed to the shared PasswordAccountFlow. -----

    private async Task<AccountFlowResult> CreateAccountWithPassword(string username, string password)
    {
        Mediator.Publish(new NotificationMessage("Account creation started",
            "Registering a character key with the selected service...", NotificationType.Info, TimeSpan.FromSeconds(5)));
        _logger.LogInformation("Starting password account creation on {server}", _serverConfigurationManager.CurrentApiUrl);

        try
        {
            var result = await _registerService.CreateAccountWithPassword(username, password, CancellationToken.None, _ => { }).ConfigureAwait(false);
            if (!result.Success)
            {
                if (result.StandaloneKeyReady)
                {
                    _secretKey = string.Empty;
                    _standaloneKeyFlow.Reset();
                    var partial = "Your key was created, saved, and assigned to this character, but the password account was not created. Snowcloak will connect using the standalone key. Add account credentials later from Settings > Service Settings.";
                    Mediator.Publish(new NotificationMessage("Standalone key created", partial, NotificationType.Warning, TimeSpan.FromSeconds(10)));
                    _ = _apiController.CreateConnections();
                    return new AccountFlowResult(true, partial);
                }

                var failure = result.ErrorMessage.IsNullOrEmpty() ? "Account setup failed. Please try again later." : result.ErrorMessage;
                Mediator.Publish(new NotificationMessage("Account creation failed", failure, NotificationType.Error, TimeSpan.FromSeconds(5)));
                return new AccountFlowResult(false, failure);
            }

            var success = string.Format(CultureInfo.InvariantCulture,
                "Account created. Stored {0} account key(s), including {1} new key(s). Attempting to connect.",
                result.SecretKeyCount, result.NewSecretKeyCount);
            Mediator.Publish(new NotificationMessage("Account created", success, NotificationType.Info, TimeSpan.FromSeconds(5)));
            _ = _apiController.CreateConnections();
            return new AccountFlowResult(true, success);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Account setup failed");
            Mediator.Publish(new NotificationMessage("Account creation failed", "Account setup failed. Please try again later.", NotificationType.Error, TimeSpan.FromSeconds(5)));
            return new AccountFlowResult(false, "Account setup failed. Please try again later.");
        }
    }

    private async Task<AccountFlowResult> SignInWithPassword(string username, string password)
    {
        Mediator.Publish(new NotificationMessage("Account sign-in started",
            "Signing in to the selected service...", NotificationType.Info, TimeSpan.FromSeconds(5)));
        _logger.LogInformation("Starting password account sign-in on {server}", _serverConfigurationManager.CurrentApiUrl);

        try
        {
            var result = await _registerService.LoginWithPassword(username, password, CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                var failure = result.ErrorMessage.IsNullOrEmpty() ? "Account login failed. Please try again later." : result.ErrorMessage;
                Mediator.Publish(new NotificationMessage("Account sign-in failed", failure, NotificationType.Error, TimeSpan.FromSeconds(5)));
                return new AccountFlowResult(false, failure);
            }

            _secretKey = string.Empty;
            _standaloneKeyFlow.Reset();
            var success = string.Format(CultureInfo.InvariantCulture,
                "Account login succeeded. Stored {0} account key(s), including {1} new key(s). Attempting to connect.",
                result.SecretKeyCount, result.NewSecretKeyCount);
            Mediator.Publish(new NotificationMessage("Account sign-in succeeded", success, NotificationType.Info, TimeSpan.FromSeconds(5)));
            _ = _apiController.CreateConnections();
            return new AccountFlowResult(true, success);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Account login failed");
            Mediator.Publish(new NotificationMessage("Account sign-in failed", "Account login failed. Please try again later.", NotificationType.Error, TimeSpan.FromSeconds(5)));
            return new AccountFlowResult(false, "Account login failed. Please try again later.");
        }
    }

    private Vector4 GetConnectionColor()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => ImGuiColors.DalamudYellow,
            ServerState.Reconnecting => ImGuiColors.DalamudRed,
            ServerState.Connected => ImGuiColors.HealerGreen,
            ServerState.Disconnected => ImGuiColors.DalamudYellow,
            ServerState.Disconnecting => ImGuiColors.DalamudYellow,
            ServerState.Unauthorized => ImGuiColors.DalamudRed,
            ServerState.VersionMisMatch => ImGuiColors.DalamudRed,
            ServerState.Offline => ImGuiColors.DalamudRed,
            ServerState.RateLimited => ImGuiColors.DalamudYellow,
            ServerState.NoSecretKey => ImGuiColors.DalamudYellow,
            ServerState.MultiChara => ImGuiColors.DalamudYellow,
            _ => ImGuiColors.DalamudRed
        };
    }

    private string GetConnectionStatus()
    {
        return _apiController.ServerState switch
        {
            ServerState.Reconnecting => "Reconnecting",
            ServerState.Connecting => "Connecting",
            ServerState.Disconnected => "Disconnected",
            ServerState.Disconnecting =>"Disconnecting",
            ServerState.Unauthorized => "Unauthorized",
            ServerState.VersionMisMatch => "Version mismatch",
            ServerState.Offline => "Unavailable",
            ServerState.RateLimited => "Rate Limited",
            ServerState.NoSecretKey => "No Secret Key",
            ServerState.MultiChara => "Duplicate Characters",
            ServerState.Connected => "Connected",
            _ => string.Empty
        };
    }

}
