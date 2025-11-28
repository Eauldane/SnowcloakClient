using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI.Files;

namespace Snowcloak.PlayerData.Factories;

public class FileDownloadManagerFactory
{
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SnowMediator _snowMediator;

    public FileDownloadManagerFactory(ILoggerFactory loggerFactory, SnowMediator snowMediator, FileTransferOrchestrator fileTransferOrchestrator,
        FileCacheManager fileCacheManager)
    {
        _loggerFactory = loggerFactory;
        _snowMediator = snowMediator;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _fileCacheManager = fileCacheManager;
    }

    public FileDownloadManager Create()
    {
        return new FileDownloadManager(_loggerFactory.CreateLogger<FileDownloadManager>(), _snowMediator, _fileTransferOrchestrator, _fileCacheManager);
    }
}