using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System.Globalization;

namespace Snowcloak.UI;

public sealed class DtrEntry : DtrEntryBase
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
    private readonly SnowcloakConfigService _configService;
    private readonly SnowMediator _snowMediator;
    private readonly PairManager _pairManager;
    private string? _text;
    private string? _tooltip;
    private ElezenStrings.Colour _colors;

    public DtrEntry(ILogger<DtrEntry> logger, IDtrBar dtrBar, SnowcloakConfigService configService, SnowMediator snowMediator, PairManager pairManager, ApiController apiController)
        : base(logger, dtrBar, "Snowcloak")
    {
        _configService = configService;
        _snowMediator = snowMediator;
        _pairManager = pairManager;
        _apiController = apiController;
    }

    protected override void ResetCachedState()
    {
        _text = null;
        _tooltip = null;
        _colors = default;
    }

    protected override void ConfigureEntry(IDtrBarEntry entry)
    {
        entry.OnClick = _ => _snowMediator.Publish(new UiToggleMessage(typeof(CompactUi)));
    }

    protected override void UpdateEntry()
    {
        if (!_configService.Current.EnableDtrEntry || !_configService.Current.HasValidSetup())
        {
            if (HasVisibleEntry)
            {
                HideEntry();
            }
            return;
        }

        ShowEntry();

        string text;
        string tooltip;
        ElezenStrings.Colour colors;
        if (_apiController.IsConnected)
        {
            var pairCount = _pairManager.GetVisibleUserCount();

            text = RenderDtrStyle(_configService.Current.DtrStyle, pairCount.ToString(CultureInfo.InvariantCulture));
            if (pairCount > 0)
            {
                IEnumerable<string> visiblePairs;
                if (_configService.Current.ShowUidInDtrTooltip)
                {
                    visiblePairs = _pairManager.GetOnlineUserPairs()
                        .Where(x => x.IsVisible)
                        .Select(x => string.Format(CultureInfo.InvariantCulture, "{0} ({1})", _configService.Current.PreferNoteInDtrTooltip ? x.GetNoteOrName() : x.PlayerName, x.UserData.AliasOrUID));
                }
                else
                {
                    visiblePairs = _pairManager.GetOnlineUserPairs()
                        .Where(x => x.IsVisible)
                        .Select(x => (_configService.Current.PreferNoteInDtrTooltip ? x.GetNoteOrName() : x.PlayerName)
                                     ?? x.UserData.AliasOrUID);
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
            Entry.Text = ElezenStrings.BuildColouredString(text, colors);
            Entry.Tooltip = tooltip;
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
}
