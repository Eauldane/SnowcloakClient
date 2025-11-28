using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.SnowcloakConfiguration;
using Snowcloak.UI;

namespace Snowcloak.Services;

public class GuiHookService : DisposableMediatorSubscriberBase
{
    private readonly ILogger<GuiHookService> _logger;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SnowcloakConfigService _configService;
    private readonly INamePlateGui _namePlateGui;
    private readonly IGameConfig _gameConfig;
    private readonly IPartyList _partyList;
    private readonly PairManager _pairManager;

    private bool _isModified = false;
    private bool _namePlateRoleColorsEnabled = false;

    public GuiHookService(ILogger<GuiHookService> logger, DalamudUtilService dalamudUtil, SnowMediator mediator, SnowcloakConfigService configService,
        INamePlateGui namePlateGui, IGameConfig gameConfig, IPartyList partyList, PairManager pairManager)
        : base(logger, mediator)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _configService = configService;
        _namePlateGui = namePlateGui;
        _gameConfig = gameConfig;
        _partyList = partyList;
        _pairManager = pairManager;

        _namePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;
        _namePlateGui.RequestRedraw();

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => GameSettingsCheck());
        Mediator.Subscribe<PairHandlerVisibleMessage>(this, (_) => RequestRedraw());
        Mediator.Subscribe<NameplateRedrawMessage>(this, (_) => RequestRedraw());
    }

    public void RequestRedraw(bool force = false)
    {
        if (!_configService.Current.UseNameColors)
        {
            if (!_isModified && !force)
                return;
            _isModified = false;
        }

        _ = Task.Run(async () => {
            await _dalamudUtil.RunOnFrameworkThread(() => _namePlateGui.RequestRedraw()).ConfigureAwait(false);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _namePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;

        _ = Task.Run(async () => {
            await _dalamudUtil.RunOnFrameworkThread(() => _namePlateGui.RequestRedraw()).ConfigureAwait(false);
        });
    }

    private void OnNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!_configService.Current.UseNameColors)
            return;

        var visibleUsers = _pairManager.GetOnlineUserPairs().Where(u => u.IsVisible && u.PlayerCharacterId != uint.MaxValue);
        var visibleUsersIds = visibleUsers.Select(u => (ulong)u.PlayerCharacterId).ToHashSet();

        var visibleUsersDict = visibleUsers.ToDictionary(u => (ulong)u.PlayerCharacterId);

        var partyMembers = new nint[_partyList.Count];

        for (int i = 0; i < _partyList.Count; ++i)
            partyMembers[i] = _partyList[i]?.GameObject?.Address ?? nint.MaxValue;

        foreach (var handler in handlers)
        {
            if (handler != null && visibleUsersIds.Contains(handler.GameObjectId))
            {
                if (_namePlateRoleColorsEnabled && partyMembers.Contains(handler.GameObject?.Address ?? nint.MaxValue))
                    continue;
                var pair = visibleUsersDict[handler.GameObjectId];
                var colors = !pair.IsApplicationBlocked ? _configService.Current.NameColors : _configService.Current.BlockedNameColors;
                handler.NameParts.TextWrap = (
                    BuildColorStartSeString(colors),
                    BuildColorEndSeString(colors)
                );
                _isModified = true;
            }
        }
    }

    private void GameSettingsCheck()
    {
        if (!_gameConfig.TryGet(Dalamud.Game.Config.UiConfigOption.NamePlateSetRoleColor, out bool namePlateRoleColorsEnabled))
            return;

        if (_namePlateRoleColorsEnabled != namePlateRoleColorsEnabled)
        {
            _namePlateRoleColorsEnabled = namePlateRoleColorsEnabled;
            RequestRedraw(force: true);
        }
    }

    #region Colored SeString
    private const byte _colorTypeForeground = 0x13;
    private const byte _colorTypeGlow = 0x14;

    private static SeString BuildColorStartSeString(DtrEntry.Colors colors)
    {
        var ssb = new SeStringBuilder();
        if (colors.Foreground != default)
            ssb.Add(BuildColorStartPayload(_colorTypeForeground, colors.Foreground));
        if (colors.Glow != default)
            ssb.Add(BuildColorStartPayload(_colorTypeGlow, colors.Glow));
        return ssb.Build();
    }

    private static SeString BuildColorEndSeString(DtrEntry.Colors colors)
    {
        var ssb = new SeStringBuilder();
        if (colors.Glow != default)
            ssb.Add(BuildColorEndPayload(_colorTypeGlow));
        if (colors.Foreground != default)
            ssb.Add(BuildColorEndPayload(_colorTypeForeground));
        return ssb.Build();
    }

    private static RawPayload BuildColorStartPayload(byte colorType, uint color)
        => new(unchecked([0x02, colorType, 0x05, 0xF6, byte.Max((byte)color, 0x01), byte.Max((byte)(color >> 8), 0x01), byte.Max((byte)(color >> 16), 0x01), 0x03]));

    private static RawPayload BuildColorEndPayload(byte colorType)
        => new([0x02, colorType, 0x02, 0xEC, 0x03]);
    #endregion
}
