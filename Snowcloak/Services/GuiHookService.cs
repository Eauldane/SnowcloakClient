using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Extensions;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
    private readonly PairRequestService _pairRequestService;

    private bool _isModified = false;
    private bool _namePlateRoleColorsEnabled = false;

    public GuiHookService(ILogger<GuiHookService> logger, DalamudUtilService dalamudUtil, SnowMediator mediator, SnowcloakConfigService configService,
        INamePlateGui namePlateGui, IGameConfig gameConfig, IPartyList partyList, PairManager pairManager, PairRequestService pairRequestService)
        : base(logger, mediator)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _configService = configService;
        _namePlateGui = namePlateGui;
        _gameConfig = gameConfig;
        _partyList = partyList;
        _pairManager = pairManager;
        _pairRequestService = pairRequestService;

        _namePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;
        _namePlateGui.RequestRedraw();

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => GameSettingsCheck());
        Mediator.Subscribe<PairHandlerVisibleMessage>(this, (_) => RequestRedraw());
        Mediator.Subscribe<NameplateRedrawMessage>(this, (_) => RequestRedraw());
        Mediator.Subscribe<PairingAvailabilityChangedMessage>(this, (_) => RequestRedraw(force: true));
    }

    public void RequestRedraw(bool force = false)
    {
        if (!_configService.Current.UseNameColors && !_configService.Current.PairingSystemEnabled)
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
        var useNameColors = _configService.Current.UseNameColors;
        var usePairingHighlights = _configService.Current.PairingSystemEnabled;

        var visibleUsers = _pairManager.GetOnlineUserPairs()
            .Where(u => u.IsVisible && u.PlayerCharacterId != uint.MaxValue)
            .ToList();
        var visibleUsersIds = visibleUsers.Select(u => (ulong)u.PlayerCharacterId).ToHashSet();
        var visibleUsersDict = visibleUsers.ToDictionary(u => (ulong)u.PlayerCharacterId);
        var hasVanityColors = visibleUsers.Any(pair => ShouldApplyVanityColor(pair));

        if (!useNameColors && !usePairingHighlights && !hasVanityColors)
            return;
        
        var availabilitySnapshot = usePairingHighlights
            ? _pairRequestService.GetAvailabilityFilterSnapshot()
            : default;


        var availableForPairing = usePairingHighlights
            ? availabilitySnapshot.Accepted
                .Select(id => (pc: _dalamudUtil.FindPlayerByNameHash(id), ident: id))
                .Where(tuple => tuple.pc.ObjectId != 0)
                .ToDictionary(tuple => (ulong)tuple.pc.ObjectId, tuple => tuple.ident)
            : new Dictionary<ulong, string>();

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
                if (TryGetVanityColors(pair, out var vanityColors))
                {
                    var colors = pair.IsAutoPaused ? _configService.Current.BlockedNameColors : vanityColors;
                    handler.NameParts.TextWrap = (
                        BuildColorStartSeString(colors),
                        BuildColorEndSeString(colors)
                    );
                    _isModified = true;
                    continue;
                }

                if (useNameColors)
                {
                    var colors = !pair.IsApplicationBlocked ? _configService.Current.NameColors : _configService.Current.BlockedNameColors;
                    handler.NameParts.TextWrap = (
                        BuildColorStartSeString(colors),
                        BuildColorEndSeString(colors)
                    );
                    _isModified = true;
                }
            }
            else if (usePairingHighlights && handler != null && availableForPairing.ContainsKey(handler.GameObjectId))
            {
                var colors = _configService.Current.PairRequestNameColors;
                handler.NameParts.TextWrap = (
                    BuildColorStartSeString(colors),
                    BuildColorEndSeString(colors)
                );
                _isModified = true;
            }
        }
    }

    private static bool ShouldApplyVanityColor(Pair pair)
    {
        return IsPairedForVanity(pair)
               && !pair.IsPaused
               && TryParseVanityColor(pair.UserData.DisplayColour, out _);
    }

    private static bool IsPairedForVanity(Pair pair)
    {
        if (pair.UserPair != null)
        {
            return pair.UserPair.OtherPermissions.IsPaired()
                   && pair.UserPair.OwnPermissions.IsPaired();
        }

        return pair.GroupPair.Any();
    }

    private static bool TryGetVanityColors(Pair pair, out DtrEntry.Colors colors)
    {
        colors = default;
        if (!IsPairedForVanity(pair) || pair.IsPaused)
            return false;

        return TryParseVanityColor(pair.UserData.DisplayColour, out colors);
    }

    private static bool TryParseVanityColor(string? hex, out DtrEntry.Colors colors)
    {
        colors = default;
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 6
                                           || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            return false;

        var red = (parsed >> 16) & 0xFF;
        var green = (parsed >> 8) & 0xFF;
        var blue = parsed & 0xFF;
        var bgr = (blue << 16) | (green << 8) | red;
        colors = new DtrEntry.Colors(Foreground: bgr);
        return true;
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
