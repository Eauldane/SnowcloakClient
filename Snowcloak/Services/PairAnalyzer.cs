using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.Core.Analysis;
using Snowcloak.FileCache;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Services;

public sealed class PairAnalyzer : DisposableMediatorSubscriberBase, IAsyncDisposable, IAnalysisSource
{
    private readonly AnalysisEngine _engine;
    private int _disposed;

    public PairAnalyzer(ILogger<PairAnalyzer> logger, Pair pair, SnowMediator mediator, FileCacheManager fileCacheManager, XivDataAnalyzer modelAnalyzer)
        : base(logger, mediator)
    {
        Pair = pair;
        _engine = new AnalysisEngine(
            logger,
            mediator,
            fileCacheManager,
            modelAnalyzer,
            nameof(PairAnalyzer),
            ignoreCacheEntries: false,
            AnalysisCachePathChoice.Last,
            () => Mediator.Publish(new PairDataAnalyzedMessage(Pair.UserData.UID)),
            includeImportantNotes: false,
            logSubject: Pair.UserData.UID);

#if DEBUG
        Mediator.SubscribeKeyed<PairDataAppliedMessage>(this, pair.UserData.UID, msg =>
        {
            if (msg.CharacterData != null)
            {
                _engine.QueueBaseAnalysis(msg.CharacterData);
            }
            else
            {
                _engine.Reset();
            }
        });

        var lastReceivedData = pair.LastReceivedCharacterData;
        if (lastReceivedData != null)
        {
            _engine.QueueBaseAnalysis(lastReceivedData);
        }
#endif
    }

    public Pair Pair { get; }
    public string DisplayName => Pair.UserData.AliasOrUID;
    public int CurrentFile => _engine.CurrentFile;
    public bool IsAnalysisRunning => _engine.IsAnalysisRunning;
    public int TotalFiles => _engine.TotalFiles;
    internal string LastPlayerName { get; set; } = string.Empty;

    public void CancelAnalyze()
    {
        _engine.CancelAnalyze();
    }

    public Task ComputeAnalysis(bool print = true, bool recalculate = false)
        => _engine.ComputeAnalysis(print, recalculate, () => LastPlayerName = Pair.PlayerName ?? string.Empty);

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
