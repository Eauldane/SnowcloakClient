using ElezenTools.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services;

namespace Snowcloak.FileCache;

internal sealed class CacheEvictionService : IDisposable
{
    private static readonly DateTime MinimumPlausibleTimestampUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SnowcloakConfigService _configService;
    private readonly FileCacheManager _fileDbManager;
    private readonly FileCompactor _fileCompactor;
    private readonly DatabaseService _databaseService;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _periodicCts = new();
    private readonly Task _periodicTask;

    public CacheEvictionService(ILogger logger, SnowcloakConfigService configService, FileCacheManager fileDbManager,
        FileCompactor fileCompactor, DatabaseService databaseService)
    {
        _logger = logger;
        _configService = configService;
        _fileDbManager = fileDbManager;
        _fileCompactor = fileCompactor;
        _databaseService = databaseService;

        _periodicTask = Task.Run(() => PeriodicRecalculationAsync(_periodicCts.Token));
    }

    public long FileCacheSize { get; private set; }
    public long FileCacheDriveFree { get; private set; }
    public bool StorageisNTFS { get; private set; }

    public void DetectStorageFormat()
    {
        if (string.IsNullOrEmpty(_configService.Current.CacheFolder)) return;

        try
        {
            DriveInfo di = new(new DirectoryInfo(_configService.Current.CacheFolder).Root.FullName);
            StorageisNTFS = string.Equals("NTFS", di.DriveFormat, StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation("Snowcloak Storage is on NTFS drive: {isNtfs}", StorageisNTFS);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine drive format for Storage Folder {folder}", _configService.Current.CacheFolder);
        }
    }

    private async Task PeriodicRecalculationAsync(CancellationToken token)
    {
        _logger.LogInformation("Starting Periodic Storage Directory Calculation Task");
        while (!token.IsCancellationRequested)
        {
            try
            {
                while (Service.IsOnFramework && !token.IsCancellationRequested)
                {
                    await Task.Delay(1, token).ConfigureAwait(false);
                }

                RecalculateFileCacheSize(token);
            }
            catch
            {
                // ignore
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public void RecalculateFileCacheSize(CancellationToken token)
    {
        if (string.IsNullOrEmpty(_configService.Current.CacheFolder) || !Directory.Exists(_configService.Current.CacheFolder))
        {
            FileCacheSize = 0;
            return;
        }

        FileCacheSize = -1;
        DriveInfo di = new(new DirectoryInfo(_configService.Current.CacheFolder).Root.FullName);
        try
        {
            FileCacheDriveFree = di.AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine drive size for Storage Folder {folder}", _configService.Current.CacheFolder);
        }

        var files = EnumerateStorageFiles(_configService.Current.CacheFolder, _fileDbManager.SubstFolder)
            .Select(f => new FileInfo(f))
            .Where(file => TryGetHashFromFile(file, out _))
            .ToList();
        FileCacheSize = files
            .Sum(f =>
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    return _fileCompactor.GetFileSizeOnDisk(f, StorageisNTFS);
                }
                catch
                {
                    return 0;
                }
            });

        Dictionary<string, DatabaseService.FileUsageStatistics> usageStats = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _databaseService.GetAggregatedFileUsage())
        {
            usageStats[entry.Key] = entry.Value;
        }

        var evictionMode = _configService.Current.CacheEvictionMode;
        var expirationCutoff = DateTime.UtcNow - DatabaseService.UsageRetentionPeriod;
        var expiredCandidates = files
            .Where(file => GetUsageLastSeenUtc(usageStats, file) < expirationCutoff)
            .ToList();
        foreach (var candidate in expiredCandidates)
        {
            token.ThrowIfCancellationRequested();
            if (DeleteFileAndUsage(candidate, usageStats))
            {
                files.Remove(candidate);
            }
        }

        var maxCacheInBytes = (long)(_configService.Current.MaxLocalCacheInGiB * 1024d * 1024d * 1024d);

        if (FileCacheSize < maxCacheInBytes) return;

        var maxCacheBuffer = maxCacheInBytes * 0.05d;
        var evictionCandidates = files
            .Select(file => new FileEvictionCandidate(
                file,
                GetUsageCount(usageStats, file),
                GetUsageLastSeenUtc(usageStats, file),
                GetFileLastAccessUtc(file)))
            .ToList();
        List<FileInfo> orderedFiles = evictionMode switch
        {
            CacheEvictionMode.LeastFrequentlyUsed => evictionCandidates
                .OrderBy(candidate => candidate.UsageCount)
                .ThenBy(candidate => candidate.UsageLastSeenUtc)
                .ThenBy(candidate => candidate.LastAccessUtc)
                .Select(candidate => candidate.File)
                .ToList(),
            CacheEvictionMode.ExpirationDate => evictionCandidates
                .OrderBy(candidate => candidate.UsageLastSeenUtc)
                .ThenBy(candidate => candidate.LastAccessUtc)
                .Select(candidate => candidate.File)
                .ToList(),
            _ => evictionCandidates
                .OrderBy(candidate => candidate.LastAccessUtc)
                .Select(candidate => candidate.File)
                .ToList(),
        };

        while (FileCacheSize > maxCacheInBytes - (long)maxCacheBuffer && orderedFiles.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var candidate = orderedFiles[0];
            orderedFiles.RemoveAt(0);
            if (DeleteFileAndUsage(candidate, usageStats))
            {
                files.Remove(candidate);
            }
        }

        if (FileCacheSize > maxCacheInBytes - (long)maxCacheBuffer)
        {
            _logger.LogWarning("Unable to reduce cache usage below configured threshold. Remaining files: {count}", files.Count);
        }
    }

    private bool DeleteFileAndUsage(FileInfo file, Dictionary<string, DatabaseService.FileUsageStatistics> usageStats)
    {
        long fileSize;
        try
        {
            fileSize = _fileCompactor.GetFileSizeOnDisk(file, StorageisNTFS);
        }
        catch
        {
            try
            {
                fileSize = file.Length;
            }
            catch
            {
                fileSize = 0;
            }
        }

        bool removedFromDisk = true;
        try
        {
            if (File.Exists(file.FullName))
            {
                File.Delete(file.FullName);
            }
        }
        catch (Exception ex)
        {
            removedFromDisk = false;
            _logger.LogWarning(ex, "Could not delete {file}", file.FullName);
        }

        if (removedFromDisk && File.Exists(file.FullName))
        {
            removedFromDisk = false;
        }

        if (removedFromDisk)
        {
            FileCacheSize = Math.Max(0, FileCacheSize - fileSize);
            if (TryGetHashFromFile(file, out var hash))
            {
                _databaseService.RemoveFileUsage(hash);
                usageStats.Remove(hash);
            }
        }

        return removedFromDisk;
    }

    private static bool TryGetHashFromFile(FileInfo file, out string hash)
    {
        var fileName = file.Name.Split('.')[0];
        if (fileName.Length == 64 && fileName.All(IsHexChar))
        {
            hash = fileName.ToUpperInvariant();
            return true;
        }

        hash = string.Empty;
        return false;
    }

    private static IEnumerable<string> EnumerateStorageFiles(string cacheDir, string substDir)
    {
        foreach (var file in Directory.EnumerateFiles(cacheDir, "*.*", SearchOption.AllDirectories))
        {
            if (!IsPathInsideDirectory(file, substDir))
            {
                yield return file;
            }
        }

        if (!string.IsNullOrEmpty(substDir) && Directory.Exists(substDir))
        {
            foreach (var file in Directory.EnumerateFiles(substDir, "*.*", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHexChar(char c)
    {
        return c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    private static DateTime GetUsageLastSeenUtc(Dictionary<string, DatabaseService.FileUsageStatistics> usageStats, FileInfo file)
    {
        if (TryGetHashFromFile(file, out var hash) && usageStats.TryGetValue(hash, out var stats) && stats.LastSeenUtc.HasValue)
        {
            return stats.LastSeenUtc.Value;
        }

        var fallback = GetFileLastAccessUtc(file);
        return fallback == DateTime.MinValue ? DateTime.UtcNow : fallback;
    }

    private static int GetUsageCount(Dictionary<string, DatabaseService.FileUsageStatistics> usageStats, FileInfo file)
    {
        if (TryGetHashFromFile(file, out var hash) && usageStats.TryGetValue(hash, out var stats))
        {
            return stats.SeenCount;
        }

        return 0;
    }

    private readonly record struct FileEvictionCandidate(FileInfo File, int UsageCount, DateTime UsageLastSeenUtc, DateTime LastAccessUtc);

    private static DateTime GetFileLastAccessUtc(FileInfo file)
    {
        if (TryGetFileTimestamp(file, f => f.LastAccessTimeUtc, out var timestamp)) return timestamp;
        if (TryGetFileTimestamp(file, f => f.LastWriteTimeUtc, out timestamp)) return timestamp;
        if (TryGetFileTimestamp(file, f => f.CreationTimeUtc, out timestamp)) return timestamp;
        return DateTime.MinValue;
    }

    private static bool TryGetFileTimestamp(FileInfo file, Func<FileInfo, DateTime> accessor, out DateTime timestamp)
    {
        timestamp = DateTime.MinValue;
        DateTime candidate;
        try
        {
            candidate = accessor(file);
        }
        catch
        {
            return false;
        }

        if (candidate == DateTime.MinValue || candidate == DateTime.MaxValue)
        {
            return false;
        }

        DateTime normalized = candidate.Kind switch
        {
            DateTimeKind.Utc => candidate,
            DateTimeKind.Local => candidate.ToUniversalTime(),
            _ => DateTime.SpecifyKind(candidate, DateTimeKind.Local).ToUniversalTime(),
        };

        if (normalized < MinimumPlausibleTimestampUtc)
        {
            return false;
        }

        if (normalized > DateTime.UtcNow.AddYears(1))
        {
            return false;
        }

        timestamp = normalized;
        return true;
    }

    public void Dispose()
    {
        _periodicCts.Cancel();
        try
        {
            _periodicTask.Wait();
        }
        catch (Exception ex) when (ex is TaskCanceledException or AggregateException { InnerException: TaskCanceledException })
        {
            _logger.LogDebug("Storage recalculation stopped");
        }

        _periodicCts.Dispose();
    }
}
