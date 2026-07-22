using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;

namespace Snowcloak.UI.Components;

public sealed class PairContextMenuBuilder : IHostedService
{
    private readonly BlockListStore _blockListStore;
    private readonly SnowcloakConfigService _configurationService;
    private readonly IContextMenu _contextMenu;
    private readonly SnowMediator _mediator;
    private readonly PairManager _pairManager;
    private readonly UserSafetyStore _safetyStore;

    public PairContextMenuBuilder(BlockListStore blockListStore, SnowcloakConfigService configurationService,
        IContextMenu contextMenu, SnowMediator mediator, PairManager pairManager, UserSafetyStore safetyStore)
    {
        _blockListStore = blockListStore;
        _configurationService = configurationService;
        _contextMenu = contextMenu;
        _mediator = mediator;
        _pairManager = pairManager;
        _safetyStore = safetyStore;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _contextMenu.OnMenuOpened += OnMenuOpened;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _contextMenu.OnMenuOpened -= OnMenuOpened;
        return Task.CompletedTask;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType == ContextMenuType.Inventory || !_configurationService.Current.EnableRightClickMenus) return;

        foreach (var pair in _pairManager.GetVisiblePairs())
            AddContextMenu(args, pair);
    }

    private void AddContextMenu(IMenuOpenedArgs args, Pair pair)
    {
        if (!pair.IsOnline || args.Target is not MenuTargetDefault target
            || target.TargetObjectId != pair.PlayerCharacterId || pair.IsPaused) return;

        void Add(string name, Action<IMenuItemClickedArgs>? action)
        {
            args.AddMenuItem(new MenuItem()
            {
                Name = name,
                OnClicked = action,
                PrefixColor = 526,
                PrefixChar = 'S'
            });
        }

        bool isBlocked = pair.IsApplicationBlocked;
        bool isBlacklisted = _blockListStore.IsUserBlacklisted(pair.UserData);
        bool isWhitelisted = _blockListStore.IsUserWhitelisted(pair.UserData);

        Add("Open Profile", _ => _mediator.Publish(new ProfileOpenStandaloneMessage(pair.UserData, pair, FallbackName: pair.PlayerName)));
        if (_safetyStore.IsAvailable)
            Add("Block all contact", _ => _safetyStore.Block(pair.UserData.UID));

        if (!isBlocked && !isBlacklisted)
            Add("Always Block Modded Appearance", _ => {
                    _blockListStore.AddBlacklistUser(pair.UserData);
                    pair.HoldApplication("Blacklist", maxValue: 1);
                    pair.ApplyLastReceivedData(forced: true);
                });
        else if (isBlocked && !isWhitelisted)
            Add("Always Allow Modded Appearance", _ => {
                    _blockListStore.AddWhitelistUser(pair.UserData);
                    pair.UnholdApplication("Blacklist", skipApplication: true);
                    pair.ApplyLastReceivedData(forced: true);
                });

        if (isWhitelisted)
            Add("Remove from Whitelist", _ => {
                _blockListStore.RemoveWhitelistUser(pair.UserData);
                pair.ApplyLastReceivedData(forced: true);
            });
        else if (isBlacklisted)
            Add("Remove from Blacklist", _ => {
                _blockListStore.RemoveBlacklistUser(pair.UserData);
                pair.UnholdApplication("Blacklist", skipApplication: true);
                pair.ApplyLastReceivedData(forced: true);
            });

        Add("Reapply last data", _ => pair.ApplyLastReceivedData(forced: true));

        if (pair.UserPair != null)
        {
            Add("Change Permissions", _ => _mediator.Publish(new OpenPermissionWindow(pair)));
            Add("Cycle pause state", _ => _mediator.Publish(new CyclePauseMessage(pair.UserData)));
        }
    }
}
