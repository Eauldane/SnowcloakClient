using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using ElezenTools.Services;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.FileCache;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Services.Venue;
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
    private const string _animSyncCommand = "/animsync";
    private const string _venueFinder = "/snowvenueplot";
    private const string _venueCommand = "/venue";
    private const string _syncshellCommandPrefix = "/ss";

    private readonly ApiController _apiController;
    private readonly ICommandManager _commandManager;
    private readonly SnowMediator _mediator;
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly ChatService _chatService;
    private readonly ServerRegistry _serverRegistry;
    private readonly IChatGui _chatGui;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly PairManager _pairManager;
    private readonly VenueRegistrationService _venueRegistrationService;
    private readonly Dictionary<string, CommandVerb> _snowCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _snowCommandOrder = [];
    private readonly List<string> _syncshellCommands = [];

    public CommandManagerService(ICommandManager commandManager, IChatGui chatGui, DalamudUtilService dalamudService, PerformanceCollectorService performanceCollectorService,
        ServerRegistry serverRegistry, CacheMonitor periodicFileScanner, ChatService chatService,
        ApiController apiController, SnowMediator mediator, SnowcloakConfigService snowcloakConfigService, PairManager pairManager,
        VenueRegistrationService venueRegistrationService)
    {
        _commandManager = commandManager;
        _chatGui = chatGui;
        _dalamudUtilService = dalamudService;
        _performanceCollectorService = performanceCollectorService;
        _serverRegistry = serverRegistry;
        _cacheMonitor = periodicFileScanner;
        _chatService = chatService;
        _apiController = apiController;
        _mediator = mediator;
        _snowcloakConfigService = snowcloakConfigService;
        _pairManager = pairManager;
        _venueRegistrationService = venueRegistrationService;

        RegisterSnowCommands();
        RegisterDalamudCommands();
        RegisterSyncshellCommands();
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler(_commandName);
        _commandManager.RemoveHandler(_commandName2);
        _commandManager.RemoveHandler(_commandName3);
        _commandManager.RemoveHandler(_animSyncCommand);
        _commandManager.RemoveHandler(_venueFinder);
        _commandManager.RemoveHandler(_venueCommand);
        foreach (var syncshellCommand in _syncshellCommands)
        {
            _commandManager.RemoveHandler(syncshellCommand);
        }
    }

    private void RegisterDalamudCommands()
    {
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
    }

    private void RegisterSnowCommands()
    {
        RegisterSnowCommand("help", "Show available Snowcloak commands.", ShowHelp);
        RegisterSnowCommand("toggle", "Toggle syncing, or use on/off to set it.", ToggleSync);
        RegisterSnowCommand("panic", "Toggle panic mode, reverting synced characters and blocking new applications.", TogglePanicMode);
        RegisterSnowCommand("gpose", "Open the character data hub.", _ => _mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi))));
        RegisterSnowCommand("rescan", "Run a cache scan.", _ => _cacheMonitor.InvokeScan());
        RegisterSnowCommand("perf", "Print performance stats, optionally limited by seconds.", PrintPerformanceStats);
        RegisterSnowCommand("medi", "Print mediator subscriber diagnostics.", _ => _mediator.PrintSubscriberInfo());
        RegisterSnowCommand("analyze", "Open data analysis.", _ => _mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi))));
        RegisterSnowCommand("bbtest", "Open the BBCode test window.", _ => _mediator.Publish(new UiToggleMessage(typeof(BbCodeTestUi))));
        RegisterSnowCommand("venue", "Open venues, or use ad/register.", HandleVenueCommand);
    }

    private void RegisterSnowCommand(string verb, string helpText, Action<string[]> handler)
    {
        _snowCommands[verb] = new CommandVerb(helpText, handler);
        _snowCommandOrder.Add(verb);
    }

    private void OnCommand(string command, string args)
    {
        if (string.Equals(command, _animSyncCommand, StringComparison.OrdinalIgnoreCase))
        {
            _ = AttemptAnimationSyncAsync();
            return;
        }

        var splitArgs = SplitCommandArgs(args);
        if (splitArgs.Length == 0)
        {
            ToggleMainWindow();
            return;
        }

        if (_snowCommands.TryGetValue(splitArgs[0], out var verb))
        {
            verb.Handler(splitArgs[1..]);
            return;
        }

        _chatGui.PrintError($"[Snowcloak] Unknown command: {splitArgs[0]}");
        ShowHelp([]);
    }

    private void ToggleMainWindow()
    {
        if (_snowcloakConfigService.Current.HasValidSetup())
            _mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
        else
            _mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
    }

    private void ToggleSync(string[] args)
    {
        if (_apiController.ServerState == WebAPI.SignalR.Utils.ServerState.Disconnecting)
        {
            _mediator.Publish(new NotificationMessage(
                "Snowcloak disconnecting",
                "Cannot use /toggle while Snowcloak is still disconnecting",
                NotificationType.Error));
        }

        if (_serverRegistry.CurrentServer == null) return;
        var fullPause = args.Length > 0
            ? ResolveFullPause(args[0])
            : !_serverRegistry.CurrentServer.FullPause;

        if (fullPause != _serverRegistry.CurrentServer.FullPause)
        {
            _serverRegistry.CurrentServer.FullPause = fullPause;
            _serverRegistry.Save();
            _ = _apiController.CreateConnections();
        }
    }

    private void PrintPerformanceStats(string[] args)
    {
        if (args.Length > 0 && int.TryParse(args[0], CultureInfo.InvariantCulture, out var limitBySeconds))
            _performanceCollectorService.PrintPerformanceStats(limitBySeconds);
        else
            _performanceCollectorService.PrintPerformanceStats();
    }

    private bool ResolveFullPause(string value)
    {
        if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
            return true;

        return !_serverRegistry.CurrentServer!.FullPause;
    }

    private void TogglePanicMode(string[] args)
    {
        PanicModeResult result;
        if (args.Length > 0)
        {
            if (!TryResolvePanicMode(args[0], out var enabled))
            {
                _chatGui.PrintError("[Snowcloak] Usage: /snow panic [on|off]");
                return;
            }

            result = _pairManager.SetPanicMode(enabled);
        }
        else
        {
            result = _pairManager.TogglePanicMode();
        }

        var message = result.Enabled
            ? string.Format(CultureInfo.InvariantCulture,
                "[Snowcloak] Panic mode enabled. Reverted and held {0} known pair(s). Use /snow panic again to resume syncing.",
                result.AffectedPairs)
            : string.Format(CultureInfo.InvariantCulture,
                "[Snowcloak] Panic mode disabled. Released {0} known pair(s).",
                result.AffectedPairs);

        _chatGui.Print(new XivChatEntry
        {
            Message = message,
            Type = XivChatType.SystemMessage
        });
    }

    private static bool TryResolvePanicMode(string value, out bool enabled)
    {
        if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "enable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "enabled", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
            return true;
        }

        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "disable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
            return true;
        }

        enabled = false;
        return false;
    }

    private void ShowHelp(string[] _)
    {
        var help = new StringBuilder();
        help.AppendLine("Snowcloak commands:");
        foreach (var verb in _snowCommandOrder)
        {
            var command = _snowCommands[verb];
            help.AppendFormat(CultureInfo.InvariantCulture, "/snow {0} - {1}", verb, command.HelpText);
            help.AppendLine();
        }

        help.AppendLine("/venue [ad|register] - Manage venues or advertisements.");
        help.AppendLine("/animsync - Request local animation redraws for yourself, your target, and party members.");

        _chatGui.Print(new XivChatEntry
        {
            Message = help.ToString().TrimEnd(),
            Type = XivChatType.SystemMessage
        });
    }

    private void OnVenueCommand(string command, string args)
    {
        HandleVenueCommand(SplitCommandArgs(args));
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
            Message = $"Housing plot identifier: {_dalamudUtilService.HousingString}",
            Type = XivChatType.SystemMessage
        });
    }

    private void RegisterSyncshellCommands()
    {
        for (var shellNumber = 1; shellNumber <= ChatService.SyncshellCommandMaxNumber; shellNumber++)
        {
            var commandName = $"{_syncshellCommandPrefix}{shellNumber}";
            _syncshellCommands.Add(commandName);
            _commandManager.AddHandler(commandName, new CommandInfo(OnSyncshellCommand)
            {
                ShowInHelp = false
            });
        }
    }

    private void OnSyncshellCommand(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            _chatGui.PrintError($"[Snowcloak] Usage: {command} <message>");
            return;
        }

        if (!command.StartsWith(_syncshellCommandPrefix, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(command.AsSpan(_syncshellCommandPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var shellNumber))
        {
            _chatGui.PrintError($"[Snowcloak] Invalid syncshell command: {command}");
            return;
        }

        _ = _chatService.SendSyncshellCommandAsync(shellNumber, args);
    }

    private static string[] SplitCommandArgs(string args)
    {
        return args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

        var partyMemberIds = await Service.RunOnFrameworkAsync(() =>
        {
            return _dalamudUtilService.GetPartyPlayerCharacters()
                .Select(member => member.EntityId)
                .ToList();
        }).ConfigureAwait(false);

        foreach (var partyMemberId in partyMemberIds)
        {
            var partyPair = _pairManager.GetPairByObjectId(partyMemberId);
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

        await Service.RunOnFrameworkAsync(() =>
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
                }
            }
        }).ConfigureAwait(false);

#if DEBUG
        var refreshedNames = string.Join(", ", pairsToRefresh.Select(p => p.UserData.AliasOrUID));
        _chatGui.Print(new XivChatEntry
        {
            Message = string.Format(CultureInfo.InvariantCulture, "Requested animation sync with {0}.", refreshedNames),
            Type = XivChatType.SystemMessage
        });
#endif
    }

    private sealed record CommandVerb(string HelpText, Action<string[]> Handler);
}
