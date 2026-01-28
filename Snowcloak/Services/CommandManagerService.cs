using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Snowcloak.FileCache;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Linq;
using System.Text;
using Snowcloak.PlayerData.Pairs;
using System.Threading.Tasks;
using System.Collections.Generic;
using Snowcloak.Services.Venue;

namespace Snowcloak.Services;

public sealed class CommandManagerService : IDisposable
{
    private const string _commandName = "/snow";
    private const string _commandName2 = "/snowcloak";
    private const string _commandName3 = "/sync";
    private const string _animSyncCommand = "/animsync";
    private const string _venueFinder = "/snowvenueplot";
    private const string _venueCommand = "/venue";
    private const string _ssCommandPrefix = "/ss";

    private readonly ApiController _apiController;
    private readonly ICommandManager _commandManager;
    private readonly SnowMediator _mediator;
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly ChatService _chatService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly IChatGui _chatGui;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly PairManager _pairManager;
    private readonly VenueRegistrationService _venueRegistrationService;

    public CommandManagerService(ICommandManager commandManager, IChatGui chatGui, DalamudUtilService dalamudService, PerformanceCollectorService performanceCollectorService,
        ServerConfigurationManager serverConfigurationManager, CacheMonitor periodicFileScanner, ChatService chatService,
        ApiController apiController, SnowMediator mediator, SnowcloakConfigService snowcloakConfigService, PairManager pairManager,
        VenueRegistrationService venueRegistrationService)
    {
        _commandManager = commandManager;
        _chatGui = chatGui;
        _dalamudUtilService = dalamudService;
        _performanceCollectorService = performanceCollectorService;
        _serverConfigurationManager = serverConfigurationManager;
        _cacheMonitor = periodicFileScanner;
        _chatService = chatService;
        _apiController = apiController;
        _mediator = mediator;
        _snowcloakConfigService = snowcloakConfigService;
        _pairManager = pairManager;
        _venueRegistrationService = venueRegistrationService;
        _commandManager.AddHandler(_commandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Snowcloak UI. Aliases include /snowcloak and /sync."
        });
        _commandManager.AddHandler(_commandName2, new CommandInfo(OnCommand)
        {
            ShowInHelp = false
        });
        _commandManager.AddHandler(_commandName3, new CommandInfo(OnCommand)
        {
            ShowInHelp = false
        });
        _commandManager.AddHandler(_animSyncCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Resets animation state of yourself, your target, and party members so that they all line up. Local only, does not affect unsynced players."
        });
        _commandManager.AddHandler(_venueCommand, new CommandInfo(OnVenueCommand)
        {
            HelpMessage = "Manage your venues or ads. Use /venue ad to manage advertisements."
        });
        _commandManager.AddHandler(_venueFinder, new CommandInfo(OnVenueFindCommand) { ShowInHelp = false });

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
        _commandManager.RemoveHandler(_animSyncCommand);
        _commandManager.RemoveHandler(_venueFinder);
        _commandManager.RemoveHandler(_venueCommand);
        
        for (int i = 1; i <= ChatService.CommandMaxNumber; ++i)
            _commandManager.RemoveHandler($"{_ssCommandPrefix}{i}");
    }

    private void OnCommand(string command, string args)
    {
        if (string.Equals(command, _animSyncCommand, StringComparison.OrdinalIgnoreCase))
        {
            _ = AttemptAnimationSyncAsync();
            return;
        }
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
                _mediator.Publish(new NotificationMessage(
                    "Snowcloak disconnecting",
                    "Cannot use /toggle while Snowcloak is still disconnecting",
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
        else if (string.Equals(splitArgs[0], "bbtest", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new UiToggleMessage(typeof(BbCodeTestUi)));
        }
        else if (string.Equals(splitArgs[0], "venue", StringComparison.OrdinalIgnoreCase))
        {
            HandleVenueCommand(splitArgs.Skip(1).ToArray());
        }
    }

    private void OnVenueCommand(string command, string args)
    {
        var splitArgs = args.ToLowerInvariant().Trim().Split(" ", StringSplitOptions.RemoveEmptyEntries);
        HandleVenueCommand(splitArgs);
    }

    private void HandleVenueCommand(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "ad", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new OpenVenueAdsWindowMessage(true));
            return;
        }

        if (args.Length > 0 && string.Equals(args[0], "register", StringComparison.OrdinalIgnoreCase))
        {
            _venueRegistrationService.BeginRegistrationFromCommand();
            return;
        }

        _mediator.Publish(new OpenVenueRegistryWindowMessage());
    }

    private void OnVenueFindCommand(string command, string args)
    {
        _chatGui.Print(new XivChatEntry
        {
            Message = $"Housing plot identifier: {_dalamudUtilService.GetHousingString()}",
            Type = XivChatType.SystemMessage
        });
    }
    
     private async Task AttemptAnimationSyncAsync()
    {
        var pairsToRefresh = new HashSet<Pair>();

        var targetId = _dalamudUtilService.GetTargetObjectId();
        if (targetId != null)
        {
            var targetPair = _pairManager.GetPairByObjectId(targetId.Value);
            if (targetPair != null)
                pairsToRefresh.Add(targetPair);
        }
        

        var partyMemberIds = await _dalamudUtilService.RunOnFrameworkThread(() =>
        {
            return _dalamudUtilService.GetPartyPlayerCharacters()
                .Select(member => member.EntityId)
                .ToList();
        }).ConfigureAwait(false);

        foreach (var partyMemberId in partyMemberIds)
        {
            var partyPair = _pairManager.GetPairByObjectId(partyMemberId);
            if (partyPair != null && pairsToRefresh.Add(partyPair))
                if (partyPair != null)
                    pairsToRefresh.Add(partyPair);
        }
        
        if (pairsToRefresh.Count == 0)
        {
            return;
        }
        var refreshedObjectIds = pairsToRefresh
            .Select(pair => pair.PlayerCharacterId)
            .Where(id => id != uint.MaxValue)
            .ToHashSet();

        await _dalamudUtilService.RunOnFrameworkThread(() =>
        {

            var processedIds = new HashSet<uint>();
            if (_dalamudUtilService.GetIsPlayerPresent())
            {
                var playerCharacter = _dalamudUtilService.GetPlayerCharacter();
                if (playerCharacter != null && processedIds.Add(playerCharacter.EntityId))
                {
                    _mediator.Publish(new PenumbraRedrawCharacterMessage(playerCharacter));
                }
            }

            var targetCharacter = _dalamudUtilService.GetTargetPlayerCharacter();
            if (targetCharacter != null && refreshedObjectIds.Contains(targetCharacter.EntityId)
                                        && processedIds.Add(targetCharacter.EntityId))
            {
                _mediator.Publish(new PenumbraRedrawCharacterMessage(targetCharacter));
            }

            foreach (var partyMember in _dalamudUtilService.GetPartyPlayerCharacters())
            {
                if (refreshedObjectIds.Contains(partyMember.EntityId) && processedIds.Add(partyMember.EntityId))
                {
                    _mediator.Publish(new PenumbraRedrawCharacterMessage(partyMember));
                }            }
        }).ConfigureAwait(false);
        var refreshedNames = string.Join(", ", pairsToRefresh.Select(p => p.UserData.AliasOrUID));
        #if DEBUG
        _chatGui.Print(new XivChatEntry
        {
            Message = string.Format(CultureInfo.InvariantCulture, "Requested animation sync with {0}.", refreshedNames),
            Type = XivChatType.SystemMessage
        });
        #endif
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
