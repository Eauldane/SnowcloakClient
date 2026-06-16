using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class DataAnalysisUi : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly AnalysisBrowser _browser = new();
    private readonly TextureOptimizerFlow _optimizer;

    public DataAnalysisUi(ILogger<DataAnalysisUi> logger, SnowMediator mediator,
        CharacterAnalyzer characterAnalyzer, IpcManager ipcManager,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Character Data Analysis###SnowcloakDataAnalysisUI", performanceCollectorService)
    {
        _characterAnalyzer = characterAnalyzer;
        _optimizer = new TextureOptimizerFlow(logger, ipcManager);
        WindowName = "Character Data Analysis";
        Mediator.Subscribe<CharacterDataAnalyzedMessage>(this, (_) => _browser.MarkDirty());
        SetScaledSizeConstraints(new Vector2(1100, 700), new Vector2(3840, 2160));
    }

    protected override void DrawInternal()
    {
        var extraColumns = new[] { _optimizer.BuildColumn() };
        _browser.Draw(_characterAnalyzer,
            "This window shows you all files and their sizes that are currently in use through your character and associated entities.",
            extraColumns,
            drawOptionsPanel: () => _optimizer.DrawOptionsPanel(_characterAnalyzer),
            onSelectionReset: _optimizer.ResetPlan);
    }

    public override void OnOpen()
    {
        _browser.MarkDirty();
        _browser.Reset(_optimizer.ResetPlan);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        _optimizer.Dispose();
    }
}
