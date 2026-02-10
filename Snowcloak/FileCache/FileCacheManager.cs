using Dalamud.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.CacheFile;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Lumina.Data;
using Lumina.Data.Files;
using Snowcloak.CacheFile.Enums;
using Snowcloak.Interop.GameModel;

namespace Snowcloak.FileCache;

public sealed class FileCacheManager : IHostedService
{
    public const string CachePrefix = "{cache}";
    public const string CsvSplit = "|";
    public const string PenumbraPrefix = "{penumbra}";
    public const string SubstPrefix = "{subst}";
    public const string SubstPath = "subst";
    public string CacheFolder => _configService.Current.CacheFolder;
    public string SubstFolder => CacheFolder.IsNullOrEmpty() ? string.Empty : CacheFolder.ToLowerInvariant().TrimEnd('\\') + "\\" + SubstPath;
    private readonly SnowcloakConfigService _configService;
    private readonly SnowMediator _snowMediator;
    private readonly string _csvPath;
    private readonly ConcurrentDictionary<string, List<FileCacheEntity>> _fileCaches = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _getCachesByPathsSemaphore = new(1, 1);
    private readonly Lock _fileWriteLock = new();
    private readonly IpcManager _ipcManager;
    private readonly ILogger<FileCacheManager> _logger;

    public FileCacheManager(ILogger<FileCacheManager> logger, IpcManager ipcManager, SnowcloakConfigService configService, SnowMediator snowMediator)
    {
        _logger = logger;
        _ipcManager = ipcManager;
        _configService = configService;
        _snowMediator = snowMediator;
        _csvPath = Path.Combine(configService.ConfigurationDirectory, "SnowcloakFiles.csv");
    }

    private string CsvBakPath => _csvPath + ".bak";

    public FileCacheEntity? CreateCacheEntry(string path, string? hash = null)
    {
        FileInfo fi = new(path);
        if (!fi.Exists) return null;
        _logger.LogTrace("Creating cache entry for {path}", path);
        var fullName = fi.FullName.ToLowerInvariant();
        if (!fullName.Contains(_configService.Current.CacheFolder.ToLowerInvariant(), StringComparison.Ordinal)) return null;
        string prefixedPath = fullName.Replace(_configService.Current.CacheFolder.ToLowerInvariant(), CachePrefix + "\\", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        if (hash != null)
            return CreateFileCacheEntity(fi, prefixedPath, hash);
        else
            return CreateFileCacheEntity(fi, prefixedPath);
    }

    public FileCacheEntity? CreateSubstEntry(string path)
    {
        FileInfo fi = new(path);
        if (!fi.Exists) return null;
        _logger.LogTrace("Creating substitute entry for {path}", path);
        var fullName = fi.FullName.ToLowerInvariant();
        if (!fullName.Contains(SubstFolder, StringComparison.Ordinal)) return null;
        string prefixedPath = fullName.Replace(SubstFolder, SubstPrefix + "\\", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        var fakeHash = Path.GetFileNameWithoutExtension(fi.FullName).ToUpperInvariant();
        var result = CreateFileCacheEntity(fi, prefixedPath, fakeHash);
        return result;
    }

    public FileCacheEntity? CreateFileEntry(string path)
    {
        FileInfo fi = new(path);
        if (!fi.Exists) return null;
        _logger.LogTrace("Creating file entry for {path}", path);
        var fullName = fi.FullName.ToLowerInvariant();
        if (!fullName.Contains(_ipcManager.Penumbra.ModDirectory!.ToLowerInvariant(), StringComparison.Ordinal)) return null;
        string prefixedPath = fullName.Replace(_ipcManager.Penumbra.ModDirectory!.ToLowerInvariant(), PenumbraPrefix + "\\", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        return CreateFileCacheEntity(fi, prefixedPath);
    }

    public List<FileCacheEntity> GetAllFileCaches() => _fileCaches.Values.SelectMany(v => v).ToList();

    public List<FileCacheEntity> GetAllFileCachesByHash(string hash, bool ignoreCacheEntries = false, bool validate = true)
    {
        List<FileCacheEntity> output = [];
        if (_fileCaches.TryGetValue(hash, out var fileCacheEntities))
        {
            foreach (var fileCache in fileCacheEntities.Where(c => ignoreCacheEntries ? (!c.IsCacheEntry && !c.IsSubstEntry) : true).ToList())
            {
                if (!validate) output.Add(fileCache);
                else
                {
                    var validated = GetValidatedFileCache(fileCache);
                    if (validated != null) output.Add(validated);
                }
            }
        }

        return output;
    }

    public async Task<List<FileCacheEntity>> ValidateLocalIntegrity(IProgress<(int, int, FileCacheEntity)> progress, CancellationToken cancellationToken)
    {
        _snowMediator.Publish(new HaltScanMessage(nameof(ValidateLocalIntegrity)));
        _logger.LogInformation("Validating local storage");
        var cacheEntries = _fileCaches.SelectMany(v => v.Value).Where(v => v.IsCacheEntry).ToList();
        List<FileCacheEntity> brokenEntities = [];
        int i = 0;
        foreach (var fileCache in cacheEntries)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (fileCache.IsSubstEntry) continue;

            _logger.LogInformation("Validating {file}", fileCache.ResolvedFilepath);

            progress.Report((i, cacheEntries.Count, fileCache));
            i++;
            if (!File.Exists(fileCache.ResolvedFilepath))
            {
                brokenEntities.Add(fileCache);
                continue;
            }

            try
            {
                var computedHash = await Crypto.GetFileHashAsync(fileCache.ResolvedFilepath).ConfigureAwait(false);
                if (!string.Equals(computedHash, fileCache.Hash, StringComparison.Ordinal))
                {
                    _logger.LogInformation("Failed to validate {file}, got hash {hash}, expected hash {expectedHash}", fileCache.ResolvedFilepath, computedHash, fileCache.Hash);
                    brokenEntities.Add(fileCache);
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Error during validation of {file}", fileCache.ResolvedFilepath);
                brokenEntities.Add(fileCache);
            }
        }

        foreach (var brokenEntity in brokenEntities)
        {
            RemoveHashedFile(brokenEntity.Hash, brokenEntity.PrefixedFilePath);

            try
            {
                File.Delete(brokenEntity.ResolvedFilepath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete {file}", brokenEntity.ResolvedFilepath);
            }
        }

        _snowMediator.Publish(new ResumeScanMessage(nameof(ValidateLocalIntegrity)));
        return brokenEntities;
    }

    public string GetCacheFilePath(string hash, string extension)
    {
        return Path.Combine(_configService.Current.CacheFolder, hash + "." + extension);
    }

    public string GetSubstFilePath(string hash, string extension)
    {
        return Path.Combine(SubstFolder, hash + "." + extension);
    }

    public async Task<(string, byte[])> GetCompressedFileData(string fileHash, CancellationToken uploadToken, IReadOnlyDictionary<string, byte[]>? optionalMetadataFields = null)
    {
        var fileCache = GetFileCacheByHash(fileHash)!;
        await using var fs = File.OpenRead(fileCache.ResolvedFilepath);
        using var ms = new MemoryStream(64 * 1024);

        var extension = Path.GetExtension(fileCache.ResolvedFilepath);
        var fileExtension = SupportedFileTypes.ParseFileExtension(extension);
        var metadata = CalculateFileMetadata(fileCache.ResolvedFilepath, fileExtension);
        var compressionType = ChooseCompressionType(fileCache.ResolvedFilepath, fileExtension);

        var header = await ScfFile.CreateSCFFile(
            fs,
            ms,
            fileExtension,
            null,
            uploadToken,
            _configService.Current.CompressionLevel,
            _configService.Current.UseMultithreadedCompression,
            metadata.TriangleCount,
            metadata.VramUsage,
            compressionType,
            optionalMetadata: null,
            optionalMetadataFields: optionalMetadataFields).ConfigureAwait(false);
        fileCache.CompressedSize = header.CompressedSize + ScfFile.GetHeaderLength(optionalMetadataLength: (uint)header.OptionalMetadataBytes.Length);
        return (fileHash, ms.ToArray());
    }
    
    private (long TriangleCount, long VramUsage) CalculateFileMetadata(string filePath, FileExtension fileExtension)
    {
        long triangleCount = -1;
        long vramUsage = -1;

        try
        {
            switch (fileExtension)
            {
                case FileExtension.MDL:
                    triangleCount = CalculateTriangleCount(filePath);
                    break;
                case FileExtension.TEX:
                    vramUsage = CalculateTextureVramUsage(filePath);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to calculate metadata for {file}", filePath);
        }

        return (triangleCount, vramUsage);
    }

    private CompressionType ChooseCompressionType(string filePath, FileExtension fileExtension)
    {
        switch (fileExtension)
        {
            case FileExtension.SCD:
                return CompressionType.LZ4;
            // Shader packs... probably compiled binaries? Google is being a bitch, correct this later if needed.
            case FileExtension.SHPK:
                return CompressionType.LZ4;
            default:
                return CompressionType.ZSTD;
        }
    }
    
    private long CalculateTriangleCount(string filePath)
    {
        try
        {
            var file = new Interop.GameModel.MdlFile(filePath);
            if (file.LodCount <= 0)
                return -1;

            for (int i = 0; i < file.LodCount; i++)
            {
                try
                {
                    var meshIdx = file.Lods[i].MeshIndex;
                    var meshCnt = file.Lods[i].MeshCount;
                    var tris = file.Meshes.Skip(meshIdx).Take(meshCnt).Sum(p => p.IndexCount) / 3;

                    if (tris > 0)
                        return tris;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not load lod mesh {mesh} from path {path}", i, filePath);
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse model file {file} for triangle calculation", filePath);
        }

        return -1;
    }

    private long CalculateTextureVramUsage(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            return fileInfo.Exists ? fileInfo.Length : -1;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not calculate texture VRAM usage for {file}", filePath);
        }

        return -1;
    }

    public FileCacheEntity? GetFileCacheByHash(string hash, bool preferSubst = false)
    {
        var caches = GetFileCachesByHash(hash);
        if (preferSubst && caches.Subst != null)
            return caches.Subst;
        return caches.Penumbra ?? caches.Cache;
    }

    public (FileCacheEntity? Penumbra, FileCacheEntity? Cache, FileCacheEntity? Subst) GetFileCachesByHash(string hash)
    {
        (FileCacheEntity? Penumbra, FileCacheEntity? Cache, FileCacheEntity? Subst) result = (null, null, null);
        if (_fileCaches.TryGetValue(hash, out var hashes))
        {
            result.Penumbra = hashes.Where(p => p.PrefixedFilePath.StartsWith(PenumbraPrefix, StringComparison.Ordinal)).Select(GetValidatedFileCache).FirstOrDefault();
            result.Cache = hashes.Where(p => p.PrefixedFilePath.StartsWith(CachePrefix, StringComparison.Ordinal)).Select(GetValidatedFileCache).FirstOrDefault();
            result.Subst = hashes.Where(p => p.PrefixedFilePath.StartsWith(SubstPrefix, StringComparison.Ordinal)).Select(GetValidatedFileCache).FirstOrDefault();
        }
        return result;
    }

    private FileCacheEntity? GetFileCacheByPath(string path)
    {
        var cleanedPath = path.Replace("/", "\\", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()
            .Replace(_ipcManager.Penumbra.ModDirectory!.ToLowerInvariant(), "", StringComparison.OrdinalIgnoreCase);
        var entry = _fileCaches.SelectMany(v => v.Value).FirstOrDefault(f => f.ResolvedFilepath.EndsWith(cleanedPath, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            _logger.LogDebug("Found no entries for {path}", cleanedPath);
            return CreateFileEntry(path);
        }

        var validatedCacheEntry = GetValidatedFileCache(entry);

        return validatedCacheEntry;
    }

    public Dictionary<string, FileCacheEntity?> GetFileCachesByPaths(string[] paths)
    {
        _getCachesByPathsSemaphore.Wait();

        try
        {
            var cleanedPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p,
                p => p.Replace("/", "\\", StringComparison.OrdinalIgnoreCase)
                    .Replace(_ipcManager.Penumbra.ModDirectory!, _ipcManager.Penumbra.ModDirectory!.EndsWith('\\') ? PenumbraPrefix + '\\' : PenumbraPrefix, StringComparison.OrdinalIgnoreCase)
                    .Replace(SubstFolder, SubstPrefix, StringComparison.OrdinalIgnoreCase)
                    .Replace(_configService.Current.CacheFolder, _configService.Current.CacheFolder.EndsWith('\\') ? CachePrefix + '\\' : CachePrefix, StringComparison.OrdinalIgnoreCase)
                    .Replace("\\\\", "\\", StringComparison.Ordinal),
                StringComparer.OrdinalIgnoreCase);

            Dictionary<string, FileCacheEntity?> result = new(StringComparer.OrdinalIgnoreCase);

            var dict = _fileCaches.SelectMany(f => f.Value)
                .ToDictionary(d => d.PrefixedFilePath, d => d, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in cleanedPaths)
            {
                //_logger.LogDebug("Checking {path}", entry.Value);

                if (dict.TryGetValue(entry.Value, out var entity))
                {
                    var validatedCache = GetValidatedFileCache(entity);
                    result.Add(entry.Key, validatedCache);
                }
                else
                {
                    if (entry.Value.StartsWith(PenumbraPrefix, StringComparison.Ordinal))
                        result.Add(entry.Key, CreateFileEntry(entry.Key));
                    else if (entry.Value.StartsWith(SubstPrefix, StringComparison.Ordinal))
                        result.Add(entry.Key, CreateSubstEntry(entry.Key));
                    else if (entry.Value.StartsWith(CachePrefix, StringComparison.Ordinal))
                        result.Add(entry.Key, CreateCacheEntry(entry.Key));
                }
            }

            return result;
        }
        finally
        {
            _getCachesByPathsSemaphore.Release();
        }
    }

    public void RemoveHashedFile(string hash, string prefixedFilePath)
    {
        if (_fileCaches.TryGetValue(hash, out var caches))
        {
            var removedCount = caches?.RemoveAll(c => string.Equals(c.PrefixedFilePath, prefixedFilePath, StringComparison.Ordinal));
            _logger.LogTrace("Removed from DB: {count} file(s) with hash {hash} and file cache {path}", removedCount, hash, prefixedFilePath);

            if (caches?.Count == 0)
            {
                _fileCaches.Remove(hash, out var _);
            }
        }
    }

    public void UpdateHashedFile(FileCacheEntity fileCache, bool computeProperties = true)
    {
        _logger.LogTrace("Updating hash for {path}", fileCache.ResolvedFilepath);
        var oldHash = fileCache.Hash;
        var prefixedPath = fileCache.PrefixedFilePath;
        if (computeProperties)
        {
            var fi = new FileInfo(fileCache.ResolvedFilepath);
            fileCache.Size = fi.Length;
            fileCache.CompressedSize = null;
            fileCache.Hash = Crypto.GetFileHashAsync(fileCache.ResolvedFilepath).GetAwaiter().GetResult();
            fileCache.LastModifiedDateTicks = fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        RemoveHashedFile(oldHash, prefixedPath);
        AddHashedFile(fileCache);
    }

    public (FileState State, FileCacheEntity FileCache) ValidateFileCacheEntity(FileCacheEntity fileCache)
    {
        fileCache = ReplacePathPrefixes(fileCache);
        FileInfo fi = new(fileCache.ResolvedFilepath);
        if (!fi.Exists)
        {
            return (FileState.RequireDeletion, fileCache);
        }
        if (!string.Equals(fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), fileCache.LastModifiedDateTicks, StringComparison.Ordinal))
        {
            return (FileState.RequireUpdate, fileCache);
        }

        return (FileState.Valid, fileCache);
    }

    public void WriteOutFullCsv()
    {
        lock (_fileWriteLock)
        {
            StringBuilder sb = new();
            foreach (var entry in _fileCaches.SelectMany(k => k.Value).OrderBy(f => f.PrefixedFilePath, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.CsvEntry);
            }

            if (File.Exists(_csvPath))
            {
                File.Copy(_csvPath, CsvBakPath, overwrite: true);
            }

            try
            {
                File.WriteAllText(_csvPath, sb.ToString());
                File.Delete(CsvBakPath);
            }
            catch
            {
                File.WriteAllText(CsvBakPath, sb.ToString());
            }
        }
    }

    internal FileCacheEntity MigrateFileHashToExtension(FileCacheEntity fileCache, string ext)
    {
        try
        {
            RemoveHashedFile(fileCache.Hash, fileCache.PrefixedFilePath);
            var extensionPath = fileCache.ResolvedFilepath.ToUpper(CultureInfo.InvariantCulture) + "." + ext;
            File.Move(fileCache.ResolvedFilepath, extensionPath, overwrite: true);
            var newHashedEntity = new FileCacheEntity(fileCache.Hash, fileCache.PrefixedFilePath + "." + ext, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            newHashedEntity.SetResolvedFilePath(extensionPath);
            AddHashedFile(newHashedEntity);
            _logger.LogTrace("Migrated from {oldPath} to {newPath}", fileCache.ResolvedFilepath, newHashedEntity.ResolvedFilepath);
            return newHashedEntity;
        }
        catch (Exception ex)
        {
            AddHashedFile(fileCache);
            _logger.LogWarning(ex, "Failed to migrate entity {entity}", fileCache.PrefixedFilePath);
            return fileCache;
        }
    }

    private void AddHashedFile(FileCacheEntity fileCache)
    {
        if (!_fileCaches.TryGetValue(fileCache.Hash, out var entries) || entries is null)
        {
            _fileCaches[fileCache.Hash] = entries = [];
        }

        if (!entries.Exists(u => string.Equals(u.PrefixedFilePath, fileCache.PrefixedFilePath, StringComparison.OrdinalIgnoreCase)))
        {
            //_logger.LogTrace("Adding to DB: {hash} => {path}", fileCache.Hash, fileCache.PrefixedFilePath);
            entries.Add(fileCache);
        }
    }

    private FileCacheEntity? CreateFileCacheEntity(FileInfo fileInfo, string prefixedPath, string? hash = null)
    {
        hash ??= Crypto.GetFileHashAsync(fileInfo.FullName).GetAwaiter().GetResult();
        var entity = new FileCacheEntity(hash, prefixedPath, fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), fileInfo.Length);
        entity = ReplacePathPrefixes(entity);
        AddHashedFile(entity);
        lock (_fileWriteLock)
        {
            File.AppendAllLines(_csvPath, new[] { entity.CsvEntry });
        }
        var result = GetFileCacheByPath(fileInfo.FullName);
        _logger.LogTrace("Creating cache entity for {name} success: {success}", fileInfo.FullName, (result != null));
        return result;
    }

    private FileCacheEntity? GetValidatedFileCache(FileCacheEntity fileCache)
    {
        var resultingFileCache = ReplacePathPrefixes(fileCache);
        //_logger.LogTrace("Validating {path}", fileCache.PrefixedFilePath);
        resultingFileCache = Validate(resultingFileCache);
        return resultingFileCache;
    }

    private FileCacheEntity ReplacePathPrefixes(FileCacheEntity fileCache)
    {
        if (fileCache.PrefixedFilePath.StartsWith(PenumbraPrefix, StringComparison.OrdinalIgnoreCase))
        {
            fileCache.SetResolvedFilePath(fileCache.PrefixedFilePath.Replace(PenumbraPrefix, _ipcManager.Penumbra.ModDirectory, StringComparison.Ordinal));
        }
        else if (fileCache.PrefixedFilePath.StartsWith(SubstPrefix, StringComparison.OrdinalIgnoreCase))
        {
            fileCache.SetResolvedFilePath(fileCache.PrefixedFilePath.Replace(SubstPrefix, SubstFolder, StringComparison.Ordinal));
        }
        else if (fileCache.PrefixedFilePath.StartsWith(CachePrefix, StringComparison.OrdinalIgnoreCase))
        {
            fileCache.SetResolvedFilePath(fileCache.PrefixedFilePath.Replace(CachePrefix, _configService.Current.CacheFolder, StringComparison.Ordinal));
        }

        return fileCache;
    }

    private FileCacheEntity? Validate(FileCacheEntity fileCache)
    {
        var file = new FileInfo(fileCache.ResolvedFilepath);
        if (!file.Exists)
        {
            RemoveHashedFile(fileCache.Hash, fileCache.PrefixedFilePath);
            return null;
        }

        if (!string.Equals(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), fileCache.LastModifiedDateTicks, StringComparison.Ordinal))
        {
            UpdateHashedFile(fileCache);
        }

        return fileCache;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting FileCacheManager");
        lock (_fileWriteLock)
        {
            try
            {
                _logger.LogInformation("Checking for {bakPath}", CsvBakPath);

                if (File.Exists(CsvBakPath))
                {
                    _logger.LogInformation("{bakPath} found, moving to {csvPath}", CsvBakPath, _csvPath);

                    File.Move(CsvBakPath, _csvPath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to move BAK to ORG, deleting BAK");
                try
                {
                    if (File.Exists(CsvBakPath))
                        File.Delete(CsvBakPath);
                }
                catch (Exception ex1)
                {
                    _logger.LogWarning(ex1, "Could not delete bak file");
                }
            }
        }

        if (File.Exists(_csvPath))
        {
            if (!_ipcManager.Penumbra.APIAvailable || string.IsNullOrEmpty(_ipcManager.Penumbra.ModDirectory))
            {
                _snowMediator.Publish(new NotificationMessage("Penumbra not connected",
                    "Could not load local file cache data. Penumbra is not connected or not properly set up. Please enable and/or configure Penumbra properly to use Snowcloak. After, reload Snowcloak in the Plugin installer.",
                    Configuration.Models.NotificationType.Error));
            }

            _logger.LogInformation("{csvPath} found, parsing", _csvPath);

            bool success = false;
            string[] entries = [];
            int attempts = 0;
            while (!success && attempts < 10)
            {
                try
                {
                    _logger.LogInformation("Attempting to read {csvPath}", _csvPath);
                    entries = File.ReadAllLines(_csvPath);
                    success = true;
                }
                catch (Exception ex)
                {
                    attempts++;
                    _logger.LogWarning(ex, "Could not open {file}, trying again", _csvPath);
                    Thread.Sleep(100);
                }
            }

            if (!entries.Any())
            {
                _logger.LogWarning("Could not load entries from {path}, continuing with empty file cache", _csvPath);
            }

            _logger.LogInformation("Found {amount} files in {path}", entries.Length, _csvPath);

            Dictionary<string, bool> processedFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var splittedEntry = entry.Split(CsvSplit, StringSplitOptions.None);
                try
                {
                    var hash = splittedEntry[0];
                    if (hash.Length != 64) throw new InvalidOperationException("Expected Hash length of 64, received " + hash.Length);
                    var path = splittedEntry[1];
                    var time = splittedEntry[2];

                    if (processedFiles.ContainsKey(path))
                    {
                        _logger.LogWarning("Already processed {file}, ignoring", path);
                        continue;
                    }

                    processedFiles.Add(path, value: true);

                    long size = -1;
                    long compressed = -1;
                    if (splittedEntry.Length > 3)
                    {
                        if (long.TryParse(splittedEntry[3], CultureInfo.InvariantCulture, out long result))
                        {
                            size = result;
                        }
                        if (long.TryParse(splittedEntry[4], CultureInfo.InvariantCulture, out long resultCompressed))
                        {
                            compressed = resultCompressed;
                        }
                    }
                    AddHashedFile(ReplacePathPrefixes(new FileCacheEntity(hash, path, time, size, compressed)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize entry {entry}, ignoring", entry);
                }
            }

            if (processedFiles.Count != entries.Length)
            {
                WriteOutFullCsv();
            }
        }

        _logger.LogInformation("Started FileCacheManager");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        WriteOutFullCsv();
        return Task.CompletedTask;
    }
}
