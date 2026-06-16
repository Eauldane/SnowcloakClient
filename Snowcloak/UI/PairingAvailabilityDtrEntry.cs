using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Pairing;
using Snowcloak.UI.PairingAvailability;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System;
using ElezenTools.UI;

namespace Snowcloak.UI;

public sealed class PairingAvailabilityDtrEntry : DtrEntryBase
{
    private readonly SnowcloakConfigService _configService;
    private readonly AvailabilityDispatcher _dispatcher;
    private readonly PairingAvailabilityStore _availabilityStore;
    private string? _text;
    private string? _valueText;
    private string? _tooltip;
    private ElezenStrings.Colour _colors;

    public PairingAvailabilityDtrEntry(ILogger<PairingAvailabilityDtrEntry> logger, IDtrBar dtrBar,
        SnowcloakConfigService configService, SnowMediator snowMediator, PairRequestService pairRequestService,
        DalamudUtilService dalamudUtilService)
        : base(logger, dtrBar, "Snowcloak Pairing")
    {
        ArgumentNullException.ThrowIfNull(pairRequestService);

        _configService = configService;
        _availabilityStore = pairRequestService.AvailabilityStore;
        _dispatcher = new AvailabilityDispatcher(logger, pairRequestService, dalamudUtilService, snowMediator);
    }

    protected override void ConfigureEntry(IDtrBarEntry entry)
    {
        entry.OnClick = _ =>
        {
            var state = _availabilityStore.State;
            _dispatcher.Dispatch(state.PendingRequestCount > 0
                ? new OpenFrostbrandPanelIntent()
                : new ToggleAvailabilityWindowIntent());
        };
    }

    protected override void ResetCachedState()
    {
        _text = null;
        _tooltip = null;
        _valueText = null;
        _colors = default;
    }

    protected override void UpdateEntry()
    {
        var availability = _availabilityStore.State;
        var pendingCount = availability.PendingRequestCount;
        var hasPending = pendingCount > 0;

        if (!_configService.Current.EnableDtrEntry || !_configService.Current.PairingSystemEnabled)
        {
            if (HasVisibleEntry)
                HideEntry();
            return;
        }

        var availabilityActive = availability.AvailabilityChannelActive;
        if (!availabilityActive && !hasPending && availability.TotalCount == 0 && availability.AutoRejectedCount == 0)
        {
            ShowUnavailable();
            return;
        }

        ShowEntry();

        var hoverPlayers = ResolveHoverPlayers(availability);
        var availableCount = hoverPlayers.Total;
        var filteredCount = hoverPlayers.FilteredCount;

        var iconText = "\uE044";
        var valueText = availableCount.ToString(CultureInfo.InvariantCulture);
        if (pendingCount > 0)
        {
            valueText += " (" + pendingCount.ToString(CultureInfo.InvariantCulture) + ")";
        }
            
        
        var tooltipLines = new List<string>();
        if (hasPending)
            tooltipLines.Add(string.Format(CultureInfo.InvariantCulture, "{0} pending pair requests", pendingCount));

        if (availabilityActive || availableCount > 0 || filteredCount > 0)
        {
            var hoverText = hoverPlayers.Count > 0
                ? string.Join(Environment.NewLine, hoverPlayers.Names)
                : availableCount > 0
                    ? string.Format(CultureInfo.InvariantCulture, "{0} users nearby", availableCount)
                    : "No nearby players open to pairing";
            var remaining = hoverPlayers.Count > 0 ? Math.Max(hoverPlayers.Total - hoverPlayers.Count, 0) : 0;

            if (remaining > 0)
                hoverText += $"{Environment.NewLine}" + string.Format(CultureInfo.InvariantCulture, "... and {0} more", remaining);
            if (filteredCount > 0)
                hoverText += $"{Environment.NewLine}" + string.Format(CultureInfo.InvariantCulture, "({0} filtered players)", filteredCount);

            var nearbyTooltip = availableCount > 0
                ? string.Format(CultureInfo.InvariantCulture,
                    availabilityActive ? "Users nearby open to pairing:{0}{1}" : "Last known users nearby open to pairing:{0}{1}",
                    Environment.NewLine, hoverText)
                : hoverText;
            tooltipLines.Add(nearbyTooltip);
        }

        if (!availabilityActive)
        {
            tooltipLines.Add("Pairing availability reconnecting");
        }

        var tooltip = string.Join(Environment.NewLine + Environment.NewLine, tooltipLines.Where(line => !string.IsNullOrWhiteSpace(line)));
        var colors = hasPending
            ? _configService.Current.DtrColorsPendingRequests
            : availableCount > 0
                ? _configService.Current.DtrColorsPairsInRange
                : _configService.Current.DtrColorsDefault;
        var fullText = string.IsNullOrWhiteSpace(valueText) ? iconText : iconText + ' ' + valueText;
        if (!_configService.Current.UseColorsInDtr)
            colors = default;

        if (!string.Equals(fullText, _text, StringComparison.Ordinal)
            || !string.Equals(valueText, _valueText, StringComparison.Ordinal)
            || !string.Equals(tooltip, _tooltip, StringComparison.Ordinal)
            || colors != _colors)
        {
            _text = fullText;
            _valueText = valueText;
            _tooltip = tooltip;
            _colors = colors;
            Entry.Text = ElezenStrings.BuildColouredString(fullText, colors);
            Entry.Tooltip = tooltip;
        }
    }

    private void ShowUnavailable()
    {
        ShowEntry();

        const string iconText = "\uE044";
        var tooltip = "Frostbrand is loading...";
        var colors = _configService.Current.DtrColorsDefault;
        if (!_configService.Current.UseColorsInDtr)
            colors = default;

        if (!string.Equals(iconText, _text, StringComparison.Ordinal)
            || _colors != colors
            || !string.Equals(tooltip, _tooltip, StringComparison.Ordinal))
        {
            _text = iconText;
            _valueText = string.Empty;
            _tooltip = tooltip;
            _colors = colors;

            Entry.Text = ElezenStrings.BuildColouredString(iconText, colors);
            Entry.Tooltip = tooltip;
        }
    }

    private static HoverPlayers ResolveHoverPlayers(AvailabilityViewState availability)
    {
        var resolved = availability.VisibleRows
            .Select(row => string.IsNullOrWhiteSpace(row.CharacterName) ? "Unnamed character" : row.CharacterName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var visible = resolved.Take(20).ToList();
        return new HoverPlayers(visible, availability.TotalCount, availability.AutoRejectedCount);
    }
    
    private readonly record struct HoverPlayers(IReadOnlyList<string> Names, int Total, int FilteredCount)
    {
        public int Count => Names.Count;
    }
}
