using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System;
using ElezenTools.UI;

namespace Snowcloak.UI;

public sealed class PairingAvailabilityDtrEntry : IDisposable, IHostedService
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly SnowcloakConfigService _configService;
    private readonly IDtrBar _dtrBar;
    private readonly Lazy<IDtrBarEntry> _entry;
    private readonly ILogger<PairingAvailabilityDtrEntry> _logger;
    private readonly SnowMediator _snowMediator;
    private readonly PairRequestService _pairRequestService;
    private readonly DalamudUtilService _dalamudUtilService;
    private string? _text;
    private string? _valueText;
    private string? _tooltip;
    private ElezenStrings.Colour _colors;
    private Task? _runTask;

    public PairingAvailabilityDtrEntry(ILogger<PairingAvailabilityDtrEntry> logger, IDtrBar dtrBar,
        SnowcloakConfigService configService, SnowMediator snowMediator, PairRequestService pairRequestService,
        DalamudUtilService dalamudUtilService)
    {
        _logger = logger;
        _dtrBar = dtrBar;
        _entry = new(CreateEntry);
        _configService = configService;
        _snowMediator = snowMediator;
        _pairRequestService = pairRequestService;
        _dalamudUtilService = dalamudUtilService;
    }

    public void Dispose()
    {
        if (_entry.IsValueCreated)
        {
            _logger.LogDebug("Disposing pairing availability DTR entry");
            Clear();
            _entry.Value.Remove();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting pairing availability DTR entry");
        _runTask = Task.Run(RunAsync, _cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource.Cancel();
        try
        {
            await _runTask!.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    private IDtrBarEntry CreateEntry()
    {
        var entry = _dtrBar.Get("Snowcloak Pairing");
        entry.OnClick = _ => _snowMediator.Publish(new UiToggleMessage(typeof(PairingAvailabilityWindow)));
        return entry;
    }

    private void Clear()
    {
        if (!_entry.IsValueCreated) return;

        _text = null;
        _tooltip = null;
        _valueText = null;
        _colors = default;
        _entry.Value.Shown = false;
    }

    private async Task RunAsync()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            await Task.Delay(1000, _cancellationTokenSource.Token).ConfigureAwait(false);
            Update();
        }
    }

    private void Update()
    {
        var pendingCount = _pairRequestService.PendingRequests.Count;
        var hasPending = pendingCount > 0;

        if (!_configService.Current.EnableDtrEntry || !_configService.Current.PairingSystemEnabled)
        {
            if (_entry.IsValueCreated && _entry.Value.Shown)
                Clear();
            return;
        }

        var availabilityActive = _pairRequestService.IsAvailabilityChannelActive;
        if (!availabilityActive && !hasPending)
        {
            ShowUnavailable();
            return;
        }

        if (!_entry.Value.Shown)
            _entry.Value.Shown = true;

        var hoverPlayers = availabilityActive ? ResolveHoverPlayers() : new HoverPlayers([], 0, 0);
        var availableCount = availabilityActive ? hoverPlayers.Total : 0;
        var filteredCount = availabilityActive ? hoverPlayers.FilteredCount : 0;

        var iconText = "\uE044";
        var valueText = availableCount.ToString();
        if (pendingCount > 0)
        {
            valueText += " (" + pendingCount.ToString() + ")";
        }
            
        
        var tooltipLines = new List<string>();
        if (hasPending)
            tooltipLines.Add(string.Format("{0} pending pair requests", pendingCount));

        if (availabilityActive)
        {
            var hoverText = hoverPlayers.Count > 0
                ? string.Join(Environment.NewLine, hoverPlayers.Names)
                : "No nearby players open to pairing";
            var remaining = Math.Max(hoverPlayers.Total - hoverPlayers.Count, 0);

            if (remaining > 0)
                hoverText += $"{Environment.NewLine}" + string.Format("... and {0} more", remaining);
            if (filteredCount > 0)
                hoverText += $"{Environment.NewLine}" + string.Format("({0} filtered players)", filteredCount);

            var nearbyTooltip = availableCount > 0
                ? string.Format("Users nearby open to pairing:{0}{1}", Environment.NewLine, hoverText)
                : hoverText;
            tooltipLines.Add(nearbyTooltip);
        }
        else
        {
            tooltipLines.Add("Pairing availability unavailable");
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

        if (!string.Equals(iconText, _text, StringComparison.Ordinal)
            || !string.Equals(valueText, _valueText, StringComparison.Ordinal)
            || !string.Equals(tooltip, _tooltip, StringComparison.Ordinal)
            || colors != _colors)
        {
            _text = fullText;
            _valueText = valueText;
            _tooltip = tooltip;
            _colors = colors;
            _entry.Value.Text = ElezenStrings.BuildColouredString(fullText, colors);
            _entry.Value.Tooltip = tooltip;
        }
    }

    private void ShowUnavailable()
    {
        if (!_entry.Value.Shown)
            _entry.Value.Shown = true;

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

            _entry.Value.Text = ElezenStrings.BuildColouredString(iconText, colors);
            _entry.Value.Tooltip = tooltip;
        }
    }

    private HoverPlayers ResolveHoverPlayers()
    {
        var availability = _pairRequestService.GetAvailabilityFilterSnapshot();
        var resolved = availability.Accepted
            .Select(ident => (ident, pc: _dalamudUtilService.FindPlayerByNameHash(ident)))
            .Where(tuple => tuple.pc.ObjectId != 0 && tuple.pc.Address != IntPtr.Zero)
            .Select(tuple => string.IsNullOrWhiteSpace(tuple.pc.Name) ? tuple.ident : tuple.pc.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var visible = resolved.Take(20).ToList();
        return new HoverPlayers(visible, resolved.Count, availability.FilteredCount);
    }
    
    private readonly record struct HoverPlayers(IReadOnlyList<string> Names, int Total, int FilteredCount)
    {
        public int Count => Names.Count;
    }
}