using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI.Files;
using Snowcloak.Infrastructure.Transfers;

namespace Snowcloak.PlayerData.Factories;

public class FileDownloadManagerFactory
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly IFileDownloadTransport _fileDownloadTransport;
    private readonly DownloadStatusStore _downloadStatusStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SnowMediator _snowMediator;
    private readonly UsageStatisticsService _usageStatisticsService;
    private readonly FileDownloadNegativeCache _negativeCache;
    private readonly Lazy<Snowcloak.FileRepair.FileRepairService> _repair;

    public FileDownloadManagerFactory(ILoggerFactory loggerFactory, SnowMediator snowMediator, FileTransferOrchestrator fileTransferOrchestrator,
        IFileDownloadTransport fileDownloadTransport, DownloadStatusStore downloadStatusStore, FileCacheManager fileCacheManager,
        UsageStatisticsService usageStatisticsService, FileDownloadNegativeCache negativeCache, Lazy<Snowcloak.FileRepair.FileRepairService> repair)
    {
        _loggerFactory = loggerFactory;
        _snowMediator = snowMediator;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileDownloadTransport = fileDownloadTransport;
        _downloadStatusStore = downloadStatusStore;
        _fileCacheManager = fileCacheManager;
        _usageStatisticsService = usageStatisticsService;
        _negativeCache = negativeCache; _repair = repair;
    }

    public FileDownloadManager Create()
    {
        return new FileDownloadManager(_loggerFactory.CreateLogger<FileDownloadManager>(), _snowMediator,
            _fileTransferOrchestrator, _fileDownloadTransport, _downloadStatusStore, _fileCacheManager,
            _usageStatisticsService, _negativeCache, _repair);
    }
}
