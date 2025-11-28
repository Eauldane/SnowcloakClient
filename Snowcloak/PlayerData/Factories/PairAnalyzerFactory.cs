using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;

namespace Snowcloak.PlayerData.Factories;

public class PairAnalyzerFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly SnowMediator _snowMediator;
    private readonly FileCacheManager _fileCacheManager;
    private readonly XivDataAnalyzer _modelAnalyzer;

    public PairAnalyzerFactory(ILoggerFactory loggerFactory, SnowMediator snowMediator,
        FileCacheManager fileCacheManager, XivDataAnalyzer modelAnalyzer)
    {
        _loggerFactory = loggerFactory;
        _fileCacheManager = fileCacheManager;
        _snowMediator = snowMediator;
        _modelAnalyzer = modelAnalyzer;
    }

    public PairAnalyzer Create(Pair pair)
    {
        return new PairAnalyzer(_loggerFactory.CreateLogger<PairAnalyzer>(), pair, _snowMediator,
            _fileCacheManager, _modelAnalyzer);
    }
}