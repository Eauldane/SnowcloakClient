using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.CacheFile;
using System.Collections.Concurrent;
using System.Globalization;
using Lumina.Data;
using Lumina.Data.Files;
using Snowcloak.CacheFile.Enums;
using Snowcloak.Infrastructure.FileCache;
using Snowcloak.Interop.GameModel;

namespace Snowcloak.FileCache;

public sealed class FileCacheManager : IHostedService
{
    public const string CachePrefix = CachePathResolver.CachePrefix;
    public const string CsvSplit = "|";
    public const string PenumbraPrefix = CachePathResolver.PenumbraPrefix;
    public const string SubstPrefix = CachePathResolver.SubstitutePrefix;
    public const string SubstPath = "subst";
    public string CacheFolder => _configService.Current.CacheFolder;
    public string SubstFolder => CreatePathResolver().SubstituteRoot;
    private readonly SnowcloakConfigService _configService;
    private readonly SnowMediator _snowMediator;
    private readonly FileCacheIndex _index;
    private readonly string _csvPath;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly ConcurrentDictionary<string, List<FileCacheEntity>> _fileCaches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileCacheEntity> _entitiesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _getCachesByPathsSemaphore = new(1, 1);
    private readonly IpcManager _ipcManager;
    private readonly ILogger<FileCacheManager> _logger;

    public FileCacheManager(ILogger<FileCacheManager> logger, IpcManager ipcManager, SnowcloakConfigService configService, SnowMediator snowMediator, FileCacheIndex index)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(ipcManager);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(snowMediator);
        ArgumentNullException.ThrowIfNull(index);

        _logger = logger;
        _ipcManager = ipcManager;
        _configService = configService;
        _snowMediator = snowMediator;
        _index = index;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _csvPath = Path.Combine(configService.ConfigurationDirectory, "SnowcloakFiles.csv");
    }

    private string CsvBakPath => _csvPath + ".bak";

    public FileCacheEntity? CreateCacheEntry(string path, string? hash = null)
    {
        return CreateEntry(CachePathRoot.Cache, path, hash, useFileNameAsHash: false);
    }

    public FileCacheEntity? CreateSubstEntry(string path)
    {
        return CreateEntry(CachePathRoot.Substitute, path, hash: null, useFileNameAsHash: true);
    }

    public FileCacheEntity? CreateFileEntry(string path, string? hash = null)
    {
        return CreateEntry(CachePathRoot.Penumbra, path, hash, useFileNameAsHash: false);
    }

    public List<FileCacheEntity> GetAllFileCaches() => _fileCaches.Values.SelectMany(v => v).ToList();

    public List<FileCacheEntity> GetAllFileCachesByHash(string hash, bool ignoreCacheEntries = false, bool validate = true)
    {
        List<FileCacheEntity> output = [];
        if (_fileCaches.TryGetValue(hash, out var fileCacheEntities))
        {
            foreach (var fileCache in fileCacheEntities.Where(c => !ignoreCacheEntries || (!c.IsCacheEntry && !c.IsSubstEntry)).ToList())
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
        return CreatePathResolver().GetObjectPath(CachePathRoot.Cache, hash, extension);
    }

    public string GetSubstFilePath(string hash, string extension)
    {
        return CreatePathResolver().GetObjectPath(CachePathRoot.Substitute, hash, extension);
    }

    public string GetTemporaryCacheFilePath(string name, string extension)
    {
        return CreatePathResolver().GetTemporaryPath(CachePathRoot.Cache, name, extension);
    }

    public async Task<(string, MemoryStream)> GetCompressedFileData(string fileHash, CancellationToken uploadToken, IReadOnlyDictionary<string, byte[]>? optionalMetadataFields = null)
    {
        var fileCache = GetFileCacheByHash(fileHash)!;
        var fs = File.OpenRead(fileCache.ResolvedFilepath);
        var ms = new MemoryStream(64 * 1024);

        await using (fs.ConfigureAwait(false))
        {
            try
            {
                var extension = Path.GetExtension(fileCache.ResolvedFilepath);
                var fileExtension = SupportedFileTypes.ParseFileExtension(extension);
                var metadata = CalculateFileMetadata(fileCache.ResolvedFilepath, fileExtension);
                var compressionType = ChooseCompressionType(fileExtension);

                var header = await ScfFile.CreateSCFFile(
                    fs,
                    ms,
                    fileExtension,
                    null,
                    uploadToken,
                    15,
                    _configService.Current.UseMultithreadedCompression,
                    metadata.TriangleCount,
                    metadata.VramUsage,
                    compressionType,
                    optionalMetadata: null,
                    optionalMetadataFields: optionalMetadataFields).ConfigureAwait(false);
                fileCache.CompressedSize = header.CompressedSize + ScfFile.GetHeaderLength(optionalMetadataLength: (uint)header.OptionalMetadataBytes.Length);
                ms.Position = 0;
                return (fileHash, ms);
            }
            catch
            {
                await ms.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
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

    private static CompressionType ChooseCompressionType(FileExtension fileExtension) =>
        fileExtension is FileExtension.SCD or FileExtension.SHPK ? CompressionType.LZ4 : CompressionType.ZSTD;
    
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

    public Dictionary<string, FileCacheEntity?> GetFileCachesByPaths(string[] paths)
    {
        _getCachesByPathsSemaphore.Wait();

        try
        {
            var resolver = CreatePathResolver();
            var cleanedPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new
                {
                    Path = path,
                    HasReference = resolver.TryCreateReference(path, out var reference),
                    Reference = reference
                })
                .Where(entry => entry.HasReference)
                .ToDictionary(entry => entry.Path, entry => entry.Reference, StringComparer.OrdinalIgnoreCase);

            Dictionary<string, FileCacheEntity?> result = new(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in cleanedPaths)
            {
                var prefixedPath = CachePathResolver.ToPrefixedPath(entry.Value);
                if (_entitiesByPath.TryGetValue(prefixedPath, out var entity))
                {
                    var validatedCache = GetValidatedFileCache(entity);
                    result.Add(entry.Key, validatedCache);
                }
                else
                {
                    result.Add(entry.Key, entry.Value.Root switch
                    {
                        CachePathRoot.Penumbra => CreateFileEntry(entry.Key),
                        CachePathRoot.Substitute => CreateSubstEntry(entry.Key),
                        CachePathRoot.Cache => CreateCacheEntry(entry.Key),
                        _ => null
                    });
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

        _entitiesByPath.TryRemove(prefixedFilePath, out _);
        _index.Remove(prefixedFilePath);
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
        var updatedFileCache = ResolveFileCacheEntity(fileCache, relocateContentAddressedFile: true);
        AddHashedFile(updatedFileCache);
        _index.Upsert(updatedFileCache);
    }

    public async Task UpdateHashedFileAsync(FileCacheEntity fileCache, bool computeProperties = true)
    {
        _logger.LogTrace("Updating hash for {path}", fileCache.ResolvedFilepath);
        var oldHash = fileCache.Hash;
        var prefixedPath = fileCache.PrefixedFilePath;
        if (computeProperties)
        {
            var fi = new FileInfo(fileCache.ResolvedFilepath);
            fileCache.Size = fi.Length;
            fileCache.CompressedSize = null;
            fileCache.Hash = await Crypto.GetFileHashAsync(fileCache.ResolvedFilepath).ConfigureAwait(false);
            fileCache.LastModifiedDateTicks = fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        RemoveHashedFile(oldHash, prefixedPath);
        var updatedFileCache = ResolveFileCacheEntity(fileCache, relocateContentAddressedFile: true);
        AddHashedFile(updatedFileCache);
        _index.Upsert(updatedFileCache);
    }

    public (FileState State, FileCacheEntity FileCache) ValidateFileCacheEntity(FileCacheEntity fileCache)
    {
        fileCache = ResolveFileCacheEntity(fileCache, relocateContentAddressedFile: true);
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

    public void WriteOutFullIndex()
    {
        _index.ReplaceAll(GetAllFileCaches());
    }

    internal FileCacheEntity MigrateFileHashToExtension(FileCacheEntity fileCache, string ext)
    {
        try
        {
            var resolver = CreatePathResolver();
            fileCache = ResolveFileCacheEntity(fileCache, relocateContentAddressedFile: false);
            if (!CachePathResolver.TryParsePrefixedPath(fileCache.PrefixedFilePath, out var sourceReference))
            {
                return fileCache;
            }

            var destinationReference = CachePathResolver.CreateObjectReference(sourceReference.Root, fileCache.Hash, ext);
            var migrated = RelocateEntity(fileCache, resolver, destinationReference);
            _logger.LogTrace("Migrated from {oldPath} to {newPath}", fileCache.ResolvedFilepath, migrated.ResolvedFilepath);
            return migrated;
        }
        catch (Exception ex)
        {
            AddHashedFile(fileCache);
            _index.Upsert(fileCache);
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
            entries.Add(fileCache);
        }

        _entitiesByPath[fileCache.PrefixedFilePath] = fileCache;
    }

    private FileCacheEntity? CreateEntry(CachePathRoot root, string path, string? hash, bool useFileNameAsHash)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists) return null;

        var resolver = CreatePathResolver();
        if (!resolver.TryCreateReference(fileInfo.FullName, out var reference) || reference.Root != root)
        {
            return null;
        }

        _logger.LogTrace("Creating {root} entry for {path}", root, path);
        hash ??= useFileNameAsHash
            ? Path.GetFileNameWithoutExtension(fileInfo.FullName)
            : Crypto.GetFileHashAsync(fileInfo.FullName).GetAwaiter().GetResult();

        return CreateFileCacheEntity(fileInfo, CachePathResolver.ToPrefixedPath(reference), hash);
    }

    private FileCacheEntity? CreateFileCacheEntity(FileInfo fileInfo, string prefixedPath, string? hash = null)
    {
        hash = (hash ?? Crypto.GetFileHashAsync(fileInfo.FullName).GetAwaiter().GetResult()).ToUpperInvariant();
        var entity = new FileCacheEntity(hash, prefixedPath, fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), fileInfo.Length);
        entity = ResolveFileCacheEntity(entity, relocateContentAddressedFile: true);
        AddHashedFile(entity);
        _index.Upsert(entity);
        _logger.LogTrace("Creating cache entity for {name} success: {success}", fileInfo.FullName, File.Exists(entity.ResolvedFilepath));
        return entity;
    }

    private FileCacheEntity? GetValidatedFileCache(FileCacheEntity fileCache)
    {
        var resultingFileCache = ResolveFileCacheEntity(fileCache, relocateContentAddressedFile: true);
        resultingFileCache = Validate(resultingFileCache);
        return resultingFileCache;
    }

    private FileCacheEntity ResolveFileCacheEntity(FileCacheEntity fileCache, bool relocateContentAddressedFile)
    {
        var resolver = CreatePathResolver();
        if (!CachePathResolver.TryParsePrefixedPath(fileCache.PrefixedFilePath, out var reference))
        {
            return fileCache;
        }

        fileCache.SetResolvedFilePath(resolver.Resolve(reference));
        if (!relocateContentAddressedFile || reference.Root is CachePathRoot.Penumbra || CachePathResolver.IsCanonicalObjectReference(reference))
        {
            return fileCache;
        }

        if (!CachePathResolver.TryGetContentAddress(reference, out _, out var extension))
        {
            return fileCache;
        }

        var destinationReference = CachePathResolver.CreateObjectReference(reference.Root, fileCache.Hash, extension);
        return RelocateEntity(fileCache, resolver, destinationReference);
    }

    private FileCacheEntity RelocateEntity(FileCacheEntity fileCache, CachePathResolver resolver, CachePathReference destinationReference)
    {
        var destinationPrefixedPath = CachePathResolver.ToPrefixedPath(destinationReference);
        if (string.Equals(fileCache.PrefixedFilePath, destinationPrefixedPath, StringComparison.OrdinalIgnoreCase))
        {
            fileCache.SetResolvedFilePath(resolver.Resolve(destinationReference));
            return fileCache;
        }

        var sourcePath = fileCache.ResolvedFilepath;
        var destinationPath = resolver.Resolve(destinationReference);
        var sourceExists = File.Exists(sourcePath);
        var destinationExists = File.Exists(destinationPath);

        if (!sourceExists && !destinationExists)
        {
            return fileCache;
        }

        try
        {
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (sourceExists)
            {
                if (destinationExists)
                {
                    File.Delete(sourcePath);
                }
                else
                {
                    File.Move(sourcePath, destinationPath);
                }
            }

            var destinationInfo = new FileInfo(destinationPath);
            var relocated = new FileCacheEntity(
                fileCache.Hash,
                destinationPrefixedPath,
                destinationInfo.Exists
                    ? destinationInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                    : fileCache.LastModifiedDateTicks,
                destinationInfo.Exists ? destinationInfo.Length : fileCache.Size,
                fileCache.CompressedSize);
            relocated.SetResolvedFilePath(destinationPath);

            RemoveHashedFile(fileCache.Hash, fileCache.PrefixedFilePath);
            AddHashedFile(relocated);
            _index.Upsert(relocated);

            return relocated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to relocate cache file {source} to {destination}", sourcePath, destinationPath);
            return fileCache;
        }
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

        ImportLegacyCsvIfPresent();

        var entities = _index.LoadAll();

        if (entities.Count > 0 && (!_ipcManager.Penumbra.APIAvailable || string.IsNullOrEmpty(_ipcManager.Penumbra.ModDirectory)))
        {
            _snowMediator.Publish(new NotificationMessage("Penumbra not connected",
                "Could not load local file cache data. Penumbra is not connected or not properly set up. Please enable and/or configure Penumbra properly to use Snowcloak. After, reload Snowcloak in the Plugin installer.",
                Configuration.Models.NotificationType.Error));
        }

        _logger.LogInformation("Found {amount} files in file cache index", entities.Count);

        foreach (var entity in entities)
        {
            AddHashedFile(ResolveFileCacheEntity(entity, relocateContentAddressedFile: false));
        }

        _logger.LogInformation("Started FileCacheManager");
        _ = _backgroundTasks.Run(() => RelocateIndexedStorageFilesAsync(_runtimeCts.Token), nameof(RelocateIndexedStorageFilesAsync));

        return Task.CompletedTask;
    }

    private async Task RelocateIndexedStorageFilesAsync(CancellationToken token)
    {
        foreach (var entity in GetAllFileCaches().Where(entity => entity.IsCacheEntry || entity.IsSubstEntry).ToList())
        {
            token.ThrowIfCancellationRequested();

            try
            {
                ResolveFileCacheEntity(entity, relocateContentAddressedFile: true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to relocate indexed cache entry {path}", entity.PrefixedFilePath);
            }

            await Task.Delay(25, token).ConfigureAwait(false);
        }
    }

    private void ImportLegacyCsvIfPresent()
    {
        try
        {
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

        if (!File.Exists(_csvPath)) return;

        _logger.LogInformation("Importing legacy file cache {csvPath} into index", _csvPath);

        bool success = false;
        string[] entries = [];
        int attempts = 0;
        while (!success && attempts < 10)
        {
            try
            {
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

        if (!success)
        {
            _logger.LogWarning("Could not read legacy file cache {path}, skipping import", _csvPath);
            return;
        }

        List<FileCacheEntity> imported = [];
        HashSet<string> processedFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var splittedEntry = entry.Split(CsvSplit, StringSplitOptions.None);
            try
            {
                var hash = splittedEntry[0];
                if (hash.Length != 64) throw new InvalidOperationException("Expected Hash length of 64, received " + hash.Length);
                var path = splittedEntry[1];
                var time = splittedEntry[2];

                if (!processedFiles.Add(path))
                {
                    _logger.LogWarning("Already processed {file}, ignoring", path);
                    continue;
                }

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
                imported.Add(new FileCacheEntity(hash, path, time, size, compressed));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to import entry {entry}, ignoring", entry);
            }
        }

        _logger.LogInformation("Importing {amount} files from {path}", imported.Count, _csvPath);
        _index.UpsertMany(imported);

        try
        {
            File.Delete(_csvPath);
            if (File.Exists(CsvBakPath))
                File.Delete(CsvBakPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete legacy file cache {path} after import", _csvPath);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _runtimeCts.CancelAsync().ConfigureAwait(false);
        _backgroundTasks.StopAccepting();
        await _backgroundTasks.StopAsync(cancellationToken).ConfigureAwait(false);
        WriteOutFullIndex();
        _runtimeCts.Dispose();
    }

    private CachePathResolver CreatePathResolver()
    {
        return new CachePathResolver(_ipcManager.Penumbra.ModDirectory, _configService.Current.CacheFolder, SubstPath);
    }
}
