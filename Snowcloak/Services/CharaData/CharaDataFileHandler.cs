using Dalamud.Game.ClientState.Objects.SubKinds;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.CharaData;
using Microsoft.Extensions.Logging;
using Snowcloak.FileCache;
using Snowcloak.PlayerData.Factories;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Services;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.WebAPI.Files;

namespace Snowcloak.Services.CharaData;

public sealed partial class CharaDataFileHandler : IDisposable
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileDownloadManager _fileDownloadManager;
    private readonly FileUploadManager _fileUploadManager;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly ILogger<CharaDataFileHandler> _logger;
    private readonly MareCharaFileDataFactory _mareCharaFileDataFactory;
    private readonly SnapshotBuilder _snapshotBuilder;
    private int _globalFileCounter;

    public CharaDataFileHandler(ILogger<CharaDataFileHandler> logger, FileDownloadManagerFactory fileDownloadManagerFactory, FileUploadManager fileUploadManager, FileCacheManager fileCacheManager,
            DalamudUtilService dalamudUtilService, GameObjectHandlerFactory gameObjectHandlerFactory, SnapshotBuilder snapshotBuilder)
    {
        ArgumentNullException.ThrowIfNull(fileDownloadManagerFactory);

        _fileDownloadManager = fileDownloadManagerFactory.Create();
        _logger = logger;
        _fileUploadManager = fileUploadManager;
        _fileCacheManager = fileCacheManager;
        _dalamudUtilService = dalamudUtilService;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _snapshotBuilder = snapshotBuilder;
        _mareCharaFileDataFactory = new(fileCacheManager);
    }

    internal void ComputeMissingFiles(CharaDataDownloadDto charaDataDownloadDto, out Dictionary<string, string> modPaths, out List<FileReplacementData> missingFiles)
    {
        ArgumentNullException.ThrowIfNull(charaDataDownloadDto);

        modPaths = [];
        missingFiles = [];
        foreach (var file in charaDataDownloadDto.FileGamePaths)
        {
            var localCacheFile = _fileCacheManager.GetFileCacheByHash(file.HashOrFileSwap);
            if (localCacheFile == null)
            {
                var existingFile = missingFiles.Find(f => string.Equals(f.Hash, file.HashOrFileSwap, StringComparison.Ordinal));
                if (existingFile == null)
                {
                    missingFiles.Add(new FileReplacementData()
                    {
                        Hash = file.HashOrFileSwap,
                        GamePaths = [file.GamePath]
                    });
                }
                else
                {
                    existingFile.GamePaths = existingFile.GamePaths.Concat([file.GamePath]).ToArray();
                }
            }
            else
            {
                modPaths[file.GamePath] = localCacheFile.ResolvedFilepath;
            }
        }

        foreach (var swap in charaDataDownloadDto.FileSwaps)
        {
            modPaths[swap.GamePath] = swap.HashOrFileSwap;
        }
    }

    public async Task<CharacterData?> CreatePlayerData()
    {
        var chara = await _dalamudUtilService.GetPlayerCharacterAsync().ConfigureAwait(false);
        if (_dalamudUtilService.IsInGpose)
        {
            chara = (IPlayerCharacter?)(await _dalamudUtilService.GetGposeCharacterFromObjectTableByNameAsync(chara.Name.TextValue, _dalamudUtilService.IsInGpose).ConfigureAwait(false));
        }

        if (chara == null)
            return null;

        using var tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player,
                        () => _dalamudUtilService.GetCharacterFromObjectTableByIndex(chara.ObjectIndex)?.Address ?? IntPtr.Zero, isWatched: false).ConfigureAwait(false);
        PlayerData.Data.CharacterData newCdata = new();
        var fragment = await _snapshotBuilder.BuildCharacterData(tempHandler, CancellationToken.None).ConfigureAwait(false);
        newCdata.SetFragment(ObjectKind.Player, fragment);
        if (newCdata.FileReplacements.TryGetValue(ObjectKind.Player, out var playerData) && playerData != null)
        {
            foreach (var data in playerData.Select(g => g.GamePaths))
            {
                data.RemoveWhere(g => g.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)
                    || g.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)
                    || g.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)
                    || (g.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)
                        && !g.Contains("/weapon/", StringComparison.OrdinalIgnoreCase)
                        && !g.Contains("/equipment/", StringComparison.OrdinalIgnoreCase))
                    || (g.EndsWith(".atex", StringComparison.OrdinalIgnoreCase)
                        && !g.Contains("/weapon/", StringComparison.OrdinalIgnoreCase)
                        && !g.Contains("/equipment/", StringComparison.OrdinalIgnoreCase)));
            }

            playerData.RemoveWhere(g => g.GamePaths.Count == 0);
        }

        return newCdata.ToAPI();
    }

    public void Dispose()
    {
        _fileDownloadManager.Dispose();
    }

    internal async Task DownloadFilesAsync(GameObjectHandler tempHandler, List<FileReplacementData> missingFiles, Dictionary<string, string> modPaths, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(tempHandler);
        ArgumentNullException.ThrowIfNull(missingFiles);
        ArgumentNullException.ThrowIfNull(modPaths);

        await _fileDownloadManager.InitiateDownloadList(tempHandler, missingFiles, token).ConfigureAwait(false);
        await _fileDownloadManager.DownloadFiles(tempHandler, missingFiles, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        foreach (var file in missingFiles.SelectMany(m => m.GamePaths, (FileEntry, GamePath) => (FileEntry.Hash, GamePath)))
        {
            var localFile = _fileCacheManager.GetFileCacheByHash(file.Hash)?.ResolvedFilepath;
            if (localFile == null)
            {
                throw new FileNotFoundException("File not found locally.");
            }
            modPaths[file.GamePath] = localFile;
        }
    }

    public Task<(MareCharaFileHeader loadedCharaFile, long expectedLength)> LoadCharaFileHeader(string filePath)
    {
        try
        {
            var result = McdfIo.ReadHeader(filePath, _logger);
            return Task.FromResult((result.Header, result.ExpectedLength));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not parse MCDF header of file {filePath}.", ex);
        }
    }

    internal Dictionary<string, string> McdfExtractFiles(MareCharaFileHeader? charaFileHeader, long expectedLength, ICollection<string> extractedFiles)
    {
        if (charaFileHeader == null) return [];

        return McdfIo.ExtractFiles(
            charaFileHeader,
            expectedLength,
            () => Path.Combine(_fileCacheManager.CacheFolder, "mare_" + _globalFileCounter++ + ".tmp"),
            extractedFiles,
            _logger);
    }

    internal async Task UpdateCharaDataAsync(CharaDataExtendedUpdateDto updateDto)
    {
        ArgumentNullException.ThrowIfNull(updateDto);

        var data = await CreatePlayerData().ConfigureAwait(false);

        if (data != null)
        {
            var hasGlamourerData = data.GlamourerData.TryGetValue(ObjectKind.Player, out var playerDataString);
            if (!hasGlamourerData) updateDto.GlamourerData = null;
            else updateDto.GlamourerData = playerDataString;

            var hasCustomizeData = data.CustomizePlusData.TryGetValue(ObjectKind.Player, out var customizeDataString);
            if (!hasCustomizeData) updateDto.CustomizeData = null;
            else updateDto.CustomizeData = customizeDataString;

            updateDto.ManipulationData = data.ManipulationData;

            var hasFiles = data.FileReplacements.TryGetValue(ObjectKind.Player, out var fileReplacements);
            if (!hasFiles)
            {
                updateDto.SetFileGamePaths([]);
                updateDto.SetFileSwaps([]);
            }
            else
            {
                updateDto.SetFileGamePaths(fileReplacements!.Where(u => string.IsNullOrEmpty(u.FileSwapPath)).SelectMany(u => u.GamePaths, (file, path) => new GamePathEntry(file.Hash, path)));
                updateDto.SetFileSwaps(fileReplacements!.Where(u => !string.IsNullOrEmpty(u.FileSwapPath)).SelectMany(u => u.GamePaths, (file, path) => new GamePathEntry(file.FileSwapPath, path)));
            }
        }
    }

    internal async Task SaveCharaFileAsync(string description, string filePath)
    {
        try
        {
            var data = await CreatePlayerData().ConfigureAwait(false);
            if (data == null) return;

            var mareCharaFileData = _mareCharaFileDataFactory.Create(description, data);
            MareCharaFileHeader header = new(MareCharaFileHeader.CurrentVersion, mareCharaFileData);
            await McdfIo.WriteAsync(
                header,
                filePath,
                hash => _fileCacheManager.GetFileCacheByHash(hash)?.ResolvedFilepath,
                _logger).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailureSavingMareCharaFile(_logger, ex);
        }
    }

    internal async Task<List<string>> UploadFiles(List<string> fileList, ValueProgress<string> uploadProgress, CancellationToken token)
    {
        return await _fileUploadManager.UploadFiles(fileList, uploadProgress, token).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "Failure Saving Mare Chara File")]
    private static partial void LogFailureSavingMareCharaFile(ILogger logger, Exception exception);
}
