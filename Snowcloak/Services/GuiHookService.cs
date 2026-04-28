using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ElezenTools.Services;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Extensions;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.UI;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Snowcloak.WebAPI;

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
    private readonly ApiController _apiController;

    private bool _isModified = false;
    private bool _isInPvP = false;
    private bool _namePlateRoleColorsEnabled = false;

    public GuiHookService(ILogger<GuiHookService> logger, DalamudUtilService dalamudUtil, SnowMediator mediator, SnowcloakConfigService configService,
        INamePlateGui namePlateGui, IGameConfig gameConfig, IPartyList partyList, PairManager pairManager, PairRequestService pairRequestService,
        ApiController apiController)
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
        _apiController = apiController;
        
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
            await Service.UseFramework(() => _namePlateGui.RequestRedraw()).ConfigureAwait(false);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _namePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;

        _ = Task.Run(async () => {
            await Service.UseFramework(() => _namePlateGui.RequestRedraw()).ConfigureAwait(false);
        });
    }

    private void OnNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        var isInPvP = _dalamudUtil.IsInPvP;
        var useNameColors = _configService.Current.UseNameColors && !isInPvP;
        var usePairingHighlights = _configService.Current.PairingSystemEnabled && !isInPvP;

        var visibleUsers = _pairManager.GetOnlineUserPairs()
            .Where(u => u.IsVisible && u.PlayerCharacterId != uint.MaxValue)
            .ToList();
        var visibleUsersIds = visibleUsers.Select(u => (ulong)u.PlayerCharacterId).ToHashSet();
        var visibleUsersDict = visibleUsers.ToDictionary(u => (ulong)u.PlayerCharacterId);
        // Yes I know doing the PvP eval first is cheaper
        var hasVanityColors = visibleUsers.Any(pair => ShouldApplyVanityColor(pair)) && !isInPvP;
        var hasSelfVanityColor = TryParseVanityColor(_apiController.DisplayColour, _apiController.DisplayGlowColour, out var selfVanityColors) && !isInPvP;
        
        if (!useNameColors && !usePairingHighlights && !hasVanityColors && !hasSelfVanityColor)
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

        var localPlayerAddress = hasSelfVanityColor ? _dalamudUtil.GetPlayerPointer() : IntPtr.Zero;
        
        foreach (var handler in handlers)
        {
            if (handler != null && hasSelfVanityColor && handler.GameObject?.Address == localPlayerAddress)
            {
                handler.NameParts.TextWrap = (
                    ElezenStrings.BuildColourStartString(selfVanityColors),
                    ElezenStrings.BuildColourEndString(selfVanityColors)
                );
                _isModified = true;
                continue;
            }
            if (handler != null && visibleUsersIds.Contains(handler.GameObjectId))
            {
                if (_namePlateRoleColorsEnabled && partyMembers.Contains(handler.GameObject?.Address ?? nint.MaxValue))
                    continue;
                var pair = visibleUsersDict[handler.GameObjectId];
                if (TryGetVanityColors(pair, out var vanityColors))
                {
                    var colors = pair.IsAutoPaused ? _configService.Current.BlockedNameColors : vanityColors;
                    handler.NameParts.TextWrap = (
                        ElezenStrings.BuildColourStartString(colors),
                        ElezenStrings.BuildColourEndString(colors)
                    );
                    _isModified = true;
                    continue;
                }

                if (useNameColors)
                {
                    var colors = !pair.IsApplicationBlocked ? _configService.Current.NameColors : _configService.Current.BlockedNameColors;
                    handler.NameParts.TextWrap = (
                        ElezenStrings.BuildColourStartString(colors),
                        ElezenStrings.BuildColourEndString(colors)
                    );
                    _isModified = true;
                }
            }
            else if (usePairingHighlights && handler != null && availableForPairing.ContainsKey(handler.GameObjectId))
            {
                var colors = _configService.Current.PairRequestNameColors;
                handler.NameParts.TextWrap = (
                    ElezenStrings.BuildColourStartString(colors),
                    ElezenStrings.BuildColourEndString(colors)
                );
                _isModified = true;
            }
        }
    }

    private static bool ShouldApplyVanityColor(Pair pair)
    {
        return IsPairedForVanity(pair)
               && !pair.IsPaused
               && TryParseVanityColor(pair.UserData.DisplayColour, pair.UserData.DisplayGlowColour, out _);
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

    private static bool TryGetVanityColors(Pair pair, out ElezenStrings.Colour colors)
    {
        colors = default;
        if (!IsPairedForVanity(pair) || pair.IsPaused)
            return false;

        return TryParseVanityColor(pair.UserData.DisplayColour, pair.UserData.DisplayGlowColour, out colors);
    }

    private static bool TryParseVanityColor(string? foregroundHex, string? glowHex, out ElezenStrings.Colour colors)
    {
        colors = default;
        var hasForeground = TryParseBgrHex(foregroundHex, out var foreground);
        var hasGlow = TryParseBgrHex(glowHex, out var glow);
        if (!hasForeground && !hasGlow)
        {
            return false;
        }

        colors = new ElezenStrings.Colour(Foreground: hasForeground ? foreground : 0u, Glow: hasGlow ? glow : 0u);
        return true;
    }

    private static bool TryParseBgrHex(string? hex, out uint bgr)
    {
        bgr = 0u;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var trimmed = hex.Trim().TrimStart('#');
        if (trimmed.Length != 6
            || !uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        var red = (parsed >> 16) & 0xFF;
        var green = (parsed >> 8) & 0xFF;
        var blue = parsed & 0xFF;
        bgr = (blue << 16) | (green << 8) | red;
        return true;
    }
    
    private void GameSettingsCheck()
    {
        var requiresRedraw = false;

        if (_gameConfig.TryGet(Dalamud.Game.Config.UiConfigOption.NamePlateSetRoleColor, out bool namePlateRoleColorsEnabled)
            && _namePlateRoleColorsEnabled != namePlateRoleColorsEnabled)
        {
            _namePlateRoleColorsEnabled = namePlateRoleColorsEnabled;
            requiresRedraw = true;
        }

        var isInPvP = _dalamudUtil.IsInPvP;
        if (_isInPvP != isInPvP)
        {
            _isInPvP = isInPvP;
            requiresRedraw = true;
        }

        if (requiresRedraw)
            RequestRedraw(force: true);
    }
}
