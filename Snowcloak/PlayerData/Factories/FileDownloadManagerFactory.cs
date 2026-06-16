using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI.Files;

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

    public FileDownloadManagerFactory(ILoggerFactory loggerFactory, SnowMediator snowMediator, FileTransferOrchestrator fileTransferOrchestrator,
        IFileDownloadTransport fileDownloadTransport, DownloadStatusStore downloadStatusStore, FileCacheManager fileCacheManager,
        UsageStatisticsService usageStatisticsService)
    {
        _loggerFactory = loggerFactory;
        _snowMediator = snowMediator;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileDownloadTransport = fileDownloadTransport;
        _downloadStatusStore = downloadStatusStore;
        _fileCacheManager = fileCacheManager;
        _usageStatisticsService = usageStatisticsService;
    }

    public FileDownloadManager Create()
    {
        return new FileDownloadManager(_loggerFactory.CreateLogger<FileDownloadManager>(), _snowMediator,
            _fileTransferOrchestrator, _fileDownloadTransport, _downloadStatusStore, _fileCacheManager, _usageStatisticsService);
    }
}
