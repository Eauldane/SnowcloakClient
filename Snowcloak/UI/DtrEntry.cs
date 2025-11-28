using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System.Runtime.InteropServices;

namespace Snowcloak.UI;

public sealed class DtrEntry : IDisposable, IHostedService
{
    private enum DtrStyle
    {
        Default,
        Style1,
        Style2,
        Style3,
        Style4,
        Style5,
        Style6,
        Style7,
        Style8,
        Style9
    }

    public const int NumStyles = 10;

    private readonly ApiController _apiController;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly SnowcloakConfigService _configService;
    private readonly IDtrBar _dtrBar;
    private readonly Lazy<IDtrBarEntry> _entry;
    private readonly ILogger<DtrEntry> _logger;
    private readonly SnowMediator _snowMediator;
    private readonly PairManager _pairManager;
    private Task? _runTask;
    private string? _text;
    private string? _tooltip;
    private Colors _colors;

    public DtrEntry(ILogger<DtrEntry> logger, IDtrBar dtrBar, SnowcloakConfigService configService, SnowMediator snowMediator, PairManager pairManager, ApiController apiController)
    {
        _logger = logger;
        _dtrBar = dtrBar;
        _entry = new(CreateEntry);
        _configService = configService;
        _snowMediator = snowMediator;
        _pairManager = pairManager;
        _apiController = apiController;
    }

    public void Dispose()
    {
        if (_entry.IsValueCreated)
        {
            _logger.LogDebug("Disposing DtrEntry");
            Clear();
            _entry.Value.Remove();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting DtrEntry");
        _runTask = Task.Run(RunAsync, _cancellationTokenSource.Token);
        _logger.LogInformation("Started DtrEntry");
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
            // ignore cancelled
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    private void Clear()
    {
        if (!_entry.IsValueCreated) return;
        _logger.LogInformation("Clearing entry");
        _text = null;
        _tooltip = null;
        _colors = default;

        _entry.Value.Shown = false;
    }

    private IDtrBarEntry CreateEntry()
    {
        _logger.LogTrace("Creating new DtrBar entry");
        var entry = _dtrBar.Get("Snowcloak");
        entry.OnClick = _ => _snowMediator.Publish(new UiToggleMessage(typeof(CompactUi)));

        return entry;
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
        if (!_configService.Current.EnableDtrEntry || !_configService.Current.HasValidSetup())
        {
            if (_entry.IsValueCreated && _entry.Value.Shown)
            {
                _logger.LogInformation("Disabling entry");

                Clear();
            }
            return;
        }

        if (!_entry.Value.Shown)
        {
            _logger.LogInformation("Showing entry");
            _entry.Value.Shown = true;
        }

        string text;
        string tooltip;
        Colors colors;
        if (_apiController.IsConnected)
        {
            var pairCount = _pairManager.GetVisibleUserCount();

            text = RenderDtrStyle(_configService.Current.DtrStyle, pairCount.ToString());
            if (pairCount > 0)
            {
                IEnumerable<string> visiblePairs;
                if (_configService.Current.ShowUidInDtrTooltip)
                {
                    visiblePairs = _pairManager.GetOnlineUserPairs()
                        .Where(x => x.IsVisible)
                        .Select(x => string.Format("{0} ({1})", _configService.Current.PreferNoteInDtrTooltip ? x.GetNoteOrName() : x.PlayerName, x.UserData.AliasOrUID));
                }
                else
                {
                    visiblePairs = _pairManager.GetOnlineUserPairs()
                        .Where(x => x.IsVisible)
                        .Select(x => string.Format("{0}", _configService.Current.PreferNoteInDtrTooltip ? x.GetNoteOrName() : x.PlayerName));
                }

                tooltip = $"Snowcloak: Connected{Environment.NewLine}----------{Environment.NewLine}{string.Join(Environment.NewLine, visiblePairs)}";
                colors = _configService.Current.DtrColorsPairsInRange;
            }
            else
            {
                tooltip = "Snowcloak: Connected";
                colors = _configService.Current.DtrColorsDefault;
            }
        }
        else
        {
            text = RenderDtrStyle(_configService.Current.DtrStyle, "\uE04C");
            tooltip = "Snowcloak: Not Connected";
            colors = _configService.Current.DtrColorsNotConnected;
        }

        if (!_configService.Current.UseColorsInDtr)
            colors = default;

        if (!string.Equals(text, _text, StringComparison.Ordinal) || !string.Equals(tooltip, _tooltip, StringComparison.Ordinal) || colors != _colors)
        {
            _text = text;
            _tooltip = tooltip;
            _colors = colors;
            _entry.Value.Text = BuildColoredSeString(text, colors);
            _entry.Value.Tooltip = tooltip;
        }
    }

    public static string RenderDtrStyle(int styleNum, string text)
    {
        var style = (DtrStyle)styleNum;

        return style switch {
            DtrStyle.Style1 => $"\xE039 {text}",
            DtrStyle.Style2 => $"\xE0BC {text}",
            DtrStyle.Style3 => $"\xE0BD {text}",
            DtrStyle.Style4 => $"\xE03A {text}",
            DtrStyle.Style5 => $"\xE033 {text}",
            DtrStyle.Style6 => $"\xE038 {text}",
            DtrStyle.Style7 => $"\xE044 {text}",
            DtrStyle.Style8 => $"\xE03C{text}",
            DtrStyle.Style9 => $"\xE040 {text} \xE041",
            _ => $"\uE05D {text}"
        };
    }

    #region Colored SeString
    private const byte _colorTypeForeground = 0x13;
    private const byte _colorTypeGlow = 0x14;

    private static SeString BuildColoredSeString(string text, Colors colors)
    {
        var ssb = new SeStringBuilder();
        if (colors.Foreground != default)
            ssb.Add(BuildColorStartPayload(_colorTypeForeground, colors.Foreground));
        if (colors.Glow != default)
            ssb.Add(BuildColorStartPayload(_colorTypeGlow, colors.Glow));
        ssb.AddText(text);
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

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct Colors(uint Foreground = default, uint Glow = default);
    #endregion
}
