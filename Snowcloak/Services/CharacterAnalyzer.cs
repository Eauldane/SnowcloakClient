using Microsoft.Extensions.Logging;
using Snowcloak.Core.Analysis;
using Snowcloak.FileCache;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Services;

public sealed class CharacterAnalyzer : DisposableMediatorSubscriberBase, IAsyncDisposable, IAnalysisSource
{
    private readonly AnalysisEngine _engine;
    private int _disposed;

    public CharacterAnalyzer(ILogger<CharacterAnalyzer> logger, SnowMediator mediator, FileCacheManager fileCacheManager, XivDataAnalyzer modelAnalyzer)
        : base(logger, mediator)
    {
        _engine = new AnalysisEngine(
            logger,
            mediator,
            fileCacheManager,
            modelAnalyzer,
            nameof(CharacterAnalyzer),
            ignoreCacheEntries: true,
            AnalysisCachePathChoice.First,
            () => Mediator.Publish(new CharacterDataAnalyzedMessage()),
            includeImportantNotes: true);

        Mediator.Subscribe<CharacterDataCreatedMessage>(this, msg => _engine.QueueBaseAnalysis(msg.CharacterData));
    }

    public string DisplayName => "your character";
    public int CurrentFile => _engine.CurrentFile;
    public bool IsAnalysisRunning => _engine.IsAnalysisRunning;
    public int TotalFiles => _engine.TotalFiles;

    public void CancelAnalyze()
    {
        _engine.CancelAnalyze();
    }

    public Task ComputeAnalysis(bool print = true, bool recalculate = false)
        => _engine.ComputeAnalysis(print, recalculate);

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing);

        if (!disposing) return;

        _engine.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing: true);
        await _engine.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal AnalysisSnapshot GetLastAnalysisSnapshot()
        => _engine.GetLastAnalysisSnapshot();

    AnalysisSnapshot IAnalysisSource.GetLastAnalysisSnapshot() => GetLastAnalysisSnapshot();
}
