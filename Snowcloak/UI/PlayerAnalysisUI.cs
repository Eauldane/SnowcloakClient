using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class PlayerAnalysisUI : WindowMediatorSubscriberBase
{
    private readonly AnalysisBrowser _browser = new();

    public PlayerAnalysisUI(ILogger<PlayerAnalysisUI> logger, Pair pair, SnowMediator mediator,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, BuildWindowTitle(pair), performanceCollectorService)
    {
        Pair = pair;
        Mediator.SubscribeKeyed<PairDataAnalyzedMessage>(this, Pair.UserData.UID, (_) => _browser.MarkDirty());
        SetScaledSizeConstraints(new Vector2(800, 600), new Vector2(3840, 2160));
        IsOpen = true;
    }

    public Pair Pair { get; private init; }
    public PairAnalyzer? PairAnalyzer => Pair.PairAnalyzer;

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    public override void OnOpen()
    {
        _browser.MarkDirty();
        _browser.Reset();
    }

    protected override void DrawInternal()
    {
        if (PairAnalyzer == null) return;

        _browser.Draw(PairAnalyzer,
            string.Format(CultureInfo.InvariantCulture, "This window shows you all files and their sizes that are currently in use by {0} and associated entities.", Pair.UserData.AliasOrUID));
    }

    private static string BuildWindowTitle(Pair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return string.Format(CultureInfo.InvariantCulture, "Character Data Analysis for {0}###SnowcloakPairAnalysis{1}", pair.UserData.AliasOrUID, pair.UserData.UID);
    }
}
