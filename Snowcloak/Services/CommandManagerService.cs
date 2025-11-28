using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using Snowcloak.FileCache;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.SnowcloakConfiguration;
using Snowcloak.UI;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Text;

namespace Snowcloak.Services;

public sealed class CommandManagerService : IDisposable
{
    private const string _commandName = "/snow";
    private const string _commandName2 = "/snowcloak";
    private const string _commandName3 = "/sync";

    private const string _ssCommandPrefix = "/ss";

    private readonly ApiController _apiController;
    private readonly ICommandManager _commandManager;
    private readonly SnowMediator _mediator;
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly ChatService _chatService;
    private readonly ServerConfigurationManager _serverConfigurationManager;

    public CommandManagerService(ICommandManager commandManager, PerformanceCollectorService performanceCollectorService,
        ServerConfigurationManager serverConfigurationManager, CacheMonitor periodicFileScanner, ChatService chatService,
        ApiController apiController, SnowMediator mediator, SnowcloakConfigService snowcloakConfigService)
    {
        _commandManager = commandManager;
        _performanceCollectorService = performanceCollectorService;
        _serverConfigurationManager = serverConfigurationManager;
        _cacheMonitor = periodicFileScanner;
        _chatService = chatService;
        _apiController = apiController;
        _mediator = mediator;
        _snowcloakConfigService = snowcloakConfigService;
        _commandManager.AddHandler(_commandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Snowcloak UI"
        });
        _commandManager.AddHandler(_commandName2, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Snowcloak UI"
        });
        _commandManager.AddHandler(_commandName3, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Snowcloak UI"
        });

        // Lazy registration of all possible /ss# commands which tbf is what the game does for linkshells anyway
        for (int i = 1; i <= ChatService.CommandMaxNumber; ++i)
        {
            _commandManager.AddHandler($"{_ssCommandPrefix}{i}", new CommandInfo(OnChatCommand)
            {
                ShowInHelp = false
            });
        }
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler(_commandName);
        _commandManager.RemoveHandler(_commandName2);
        _commandManager.RemoveHandler(_commandName3);

        for (int i = 1; i <= ChatService.CommandMaxNumber; ++i)
            _commandManager.RemoveHandler($"{_ssCommandPrefix}{i}");
    }

    private void OnCommand(string command, string args)
    {
        var splitArgs = args.ToLowerInvariant().Trim().Split(" ", StringSplitOptions.RemoveEmptyEntries);

        if (splitArgs.Length == 0)
        {
            // Interpret this as toggling the UI
            if (_snowcloakConfigService.Current.HasValidSetup())
                _mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
            else
                _mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
            return;
        }

        if (string.Equals(splitArgs[0], "toggle", StringComparison.OrdinalIgnoreCase))
        {
            if (_apiController.ServerState == WebAPI.SignalR.Utils.ServerState.Disconnecting)
            {
                _mediator.Publish(new NotificationMessage("Snowcloak disconnecting", "Cannot use /toggle while Snowcloak is still disconnecting",
                    NotificationType.Error));
            }

            if (_serverConfigurationManager.CurrentServer == null) return;
            var fullPause = splitArgs.Length > 1 ? splitArgs[1] switch
            {
                "on" => false,
                "off" => true,
                _ => !_serverConfigurationManager.CurrentServer.FullPause,
            } : !_serverConfigurationManager.CurrentServer.FullPause;

            if (fullPause != _serverConfigurationManager.CurrentServer.FullPause)
            {
                _serverConfigurationManager.CurrentServer.FullPause = fullPause;
                _serverConfigurationManager.Save();
                _ = _apiController.CreateConnections();
            }
        }
        else if (string.Equals(splitArgs[0], "gpose", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi)));
        }
        else if (string.Equals(splitArgs[0], "rescan", StringComparison.OrdinalIgnoreCase))
        {
            _cacheMonitor.InvokeScan();
        }
        else if (string.Equals(splitArgs[0], "perf", StringComparison.OrdinalIgnoreCase))
        {
            if (splitArgs.Length > 1 && int.TryParse(splitArgs[1], CultureInfo.InvariantCulture, out var limitBySeconds))
            {
                _performanceCollectorService.PrintPerformanceStats(limitBySeconds);
            }
            else
            {
                _performanceCollectorService.PrintPerformanceStats();
            }
        }
        else if (string.Equals(splitArgs[0], "medi", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.PrintSubscriberInfo();
        }
        else if (string.Equals(splitArgs[0], "analyze", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi)));
        }
    }

    private void OnChatCommand(string command, string args)
    {
        if (_snowcloakConfigService.Current.DisableSyncshellChat)
            return;

        int shellNumber = int.Parse(command[_ssCommandPrefix.Length..]);

        if (args.Length == 0)
        {
            _chatService.SwitchChatShell(shellNumber);
        }
        else
        {
            // FIXME: Chat content seems to already be stripped of any special characters here?
            byte[] chatBytes = Encoding.UTF8.GetBytes(args);
            _chatService.SendChatShell(shellNumber, chatBytes);
        }
    }
}