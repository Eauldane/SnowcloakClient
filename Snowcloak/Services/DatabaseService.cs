using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Snowcloak.Infrastructure.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;


namespace Snowcloak.Services;

public class DatabaseService : IAsyncDisposable
{
    private readonly ILogger<DatabaseService> _logger;
    private readonly SqliteDatabase _db;
    private readonly Lock _cleanupLock = new();
    private DateTime _lastCleanupUtc = DateTime.MinValue;
    private readonly CancellationTokenSource _cleanupCts = new();
    private readonly Task _cleanupTask;
    public static readonly TimeSpan UsageRetentionPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FileSeenFlushInterval = TimeSpan.FromSeconds(2);
    private const int FileSeenFlushThreshold = 50;
    private const string BucketDateFormat = "yyyy-MM-dd";

    public readonly record struct FileUsageStatistics(int SeenCount, DateTime? LastSeenUtc);
    
    private readonly record struct FileSeenEntry(string Uid, string FileHash, DateTime SeenAtUtc);

    private readonly Channel<FileSeenEntry> _fileSeenChannel = Channel.CreateUnbounded<FileSeenEntry>(new()
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly CancellationTokenSource _queueCts = new();
    private readonly Task _queueWorker;


    public DatabaseService(SqliteDatabase db,
        ILogger<DatabaseService> logger)
    {
        _logger = logger;
        _db = db;

        RunMaintenance();
        _cleanupTask = Task.Run(() => PeriodicCleanupAsync(_cleanupCts.Token));
        _queueWorker = Task.Run(() => ProcessFileSeenQueueAsync(_queueCts.Token));
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    public static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    // Temporary Home
    public void RecordFileSeen(string uid, string fileHash, DateTime seenAtUtc)
    {
        var enqueueTask = EnqueueFileSeen(uid, fileHash, seenAtUtc);
        if (!enqueueTask.IsCompletedSuccessfully)
        {
            _ = enqueueTask.AsTask();
        }
    }

        public ValueTask EnqueueFileSeen(string uid, string fileHash, DateTime seenAtUtc)
    {
        if (string.IsNullOrEmpty(fileHash)) return ValueTask.CompletedTask;
        var normalizedHash = fileHash.ToUpperInvariant();
        var normalizedTimestamp = seenAtUtc.ToUniversalTime();
        return _fileSeenChannel.Writer.WriteAsync(new(uid, normalizedHash, normalizedTimestamp), _queueCts.Token);
    }

    public IReadOnlyDictionary<string, FileUsageStatistics> GetAggregatedFileUsage()
    {
        Dictionary<string, FileUsageStatistics> usage = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var connection = _db.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT file_hash, seen_count, last_seen_at
            FROM file_hash_uid;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var hash = reader.GetString(0).ToUpperInvariant();
                int seenCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                DateTime? lastSeen = null;
                if (!reader.IsDBNull(2))
                {
                    var lastSeenRaw = reader.GetString(2);
                    if (DateTime.TryParse(lastSeenRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                            out var parsed))
                    {
                        lastSeen = parsed.ToUniversalTime();
                    }
                }

                usage[hash] = new FileUsageStatistics(seenCount, lastSeen);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load aggregated file usage statistics");
        }

        return usage;
    }

    public void RemoveFileUsage(string fileHash)
    {
        if (string.IsNullOrEmpty(fileHash)) return;

        var normalizedHash = fileHash.ToUpperInvariant();

        try
        {
            using var writeScope = _db.EnterWrite();
            using var connection = _db.Open();
            using var transaction = connection.BeginTransaction();

            using (var deleteUsage = connection.CreateCommand())
            {
                deleteUsage.Transaction = transaction;
                deleteUsage.CommandText = @"DELETE FROM file_hash_uid WHERE file_hash = $hash;";
                deleteUsage.Parameters.AddWithValue("$hash", normalizedHash);
                deleteUsage.ExecuteNonQuery();
            }

            
            using (var deleteBuckets = connection.CreateCommand())
            {
                deleteBuckets.Transaction = transaction;
                deleteBuckets.CommandText = @"DELETE FROM file_hash_seen_buckets WHERE file_hash = $hash;";
                deleteBuckets.Parameters.AddWithValue("$hash", normalizedHash);
                deleteBuckets.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to purge usage data for {hash}", normalizedHash);
        }
    }

    private bool PerformRetentionCleanup(SqliteConnection connection, SqliteTransaction? transaction, DateTime nowUtc,
        bool force)
    {
        lock (_cleanupLock)
        {
            if (!force && (nowUtc - _lastCleanupUtc) < CleanupInterval)
            {
                return false;
            }

            var ownTransaction = transaction == null;
            var workingTransaction = transaction ?? connection.BeginTransaction();
            try
            {
                var threshold = nowUtc - UsageRetentionPeriod;
                var thresholdDate = threshold.Date.ToString(BucketDateFormat, CultureInfo.InvariantCulture);
                var thresholdTimestamp = threshold.ToString("O", CultureInfo.InvariantCulture);
                
                int deletedBuckets;
                using (var deleteBuckets = connection.CreateCommand())
                {
                    deleteBuckets.Transaction = workingTransaction;
                    deleteBuckets.CommandText =
                        @"DELETE FROM file_hash_seen_buckets WHERE bucket_date < $threshold;";
                    deleteBuckets.Parameters.AddWithValue("$threshold", thresholdDate);
                    deletedBuckets = deleteBuckets.ExecuteNonQuery();
                }

                int deletedSemiTransients;
                using (var deleteSemiTransients = connection.CreateCommand())
                {
                    deleteSemiTransients.Transaction = workingTransaction;
                    deleteSemiTransients.CommandText =
                        @"DELETE FROM semi_transient_resources WHERE last_seen_at < $threshold;";
                    deleteSemiTransients.Parameters.AddWithValue("$threshold", thresholdTimestamp);
                    deletedSemiTransients = deleteSemiTransients.ExecuteNonQuery();
                }

                ExecuteNonQuery(connection, workingTransaction, "DELETE FROM file_hash_uid;");

                using (var rebuildCommand = connection.CreateCommand())
                {
                    rebuildCommand.Transaction = workingTransaction;
                    rebuildCommand.CommandText =
                        @"INSERT INTO file_hash_uid (file_hash, first_seen_at, last_seen_at, seen_count)
SELECT file_hash, MIN(bucket_date || 'T00:00:00Z'), MAX(bucket_date || 'T00:00:00Z'), SUM(count)
FROM file_hash_seen_buckets
GROUP BY file_hash;";
                    rebuildCommand.ExecuteNonQuery();
                }

                if (ownTransaction)
                {
                    workingTransaction.Commit();
                }

                _lastCleanupUtc = nowUtc;
                return deletedBuckets > 0 || deletedSemiTransients > 0;
                
            }
            catch
            {
                if (ownTransaction)
                {
                    workingTransaction.Rollback();
                }

                throw;
            }
            finally
            {
                if (ownTransaction)
                {
                    workingTransaction.Dispose();
                }
            }
        }
    }

    private async Task PeriodicCleanupAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            try
            {
                RunMaintenance();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background database maintenance failed");
            }
        }
    }
    
    private async Task ProcessFileSeenQueueAsync(CancellationToken cancellationToken)
    {
        List<FileSeenEntry> buffer = new(FileSeenFlushThreshold);
        using PeriodicTimer timer = new(FileSeenFlushInterval);
        Task<bool> timerTask = WaitForNextTick(timer, cancellationToken);
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var readTask = _fileSeenChannel.Reader.ReadAsync(cancellationToken).AsTask();
                    var completed = await Task.WhenAny(readTask, timerTask).ConfigureAwait(false);

                    if (completed == readTask)
                    {
                        buffer.Add(await readTask.ConfigureAwait(false));
                        while (buffer.Count < FileSeenFlushThreshold &&
                               _fileSeenChannel.Reader.TryRead(out var nextItem))
                        {
                            buffer.Add(nextItem);
                        }

                        if (buffer.Count >= FileSeenFlushThreshold)
                        {
                            await FlushFileSeenBufferAsync(buffer, cancellationToken).ConfigureAwait(false);
                        }

                        if (timerTask.IsCompleted)
                        {
                            timerTask = WaitForNextTick(timer, cancellationToken);
                        }
                    }
                    else
                    {
                        if (await timerTask.ConfigureAwait(false) && buffer.Count > 0)
                        {
                            await FlushFileSeenBufferAsync(buffer, cancellationToken).ConfigureAwait(false);
                        }

                        timerTask = WaitForNextTick(timer, cancellationToken);
                        
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed processing file seen queue");
                }
            }
        }
        finally
        {
            while (_fileSeenChannel.Reader.TryRead(out var remaining))
            {
                buffer.Add(remaining);
            }

            if (buffer.Count > 0)
            {
                try
                {
                    await FlushFileSeenBufferAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed flushing remaining file seen entries");
                }
            }
        }
    }

    private static Task<bool> WaitForNextTick(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        var wait = timer.WaitForNextTickAsync(cancellationToken);
        return wait.IsCompletedSuccessfully ? Task.FromResult(wait.Result) : wait.AsTask();
    }

    private async Task FlushFileSeenBufferAsync(List<FileSeenEntry> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Count == 0) return;

        try
        {
            using var writeScope = await _db.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
            using var connection = await _db.OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var nowUtc = DateTime.UtcNow;
            bool shouldVacuum = PerformRetentionCleanup(connection, transaction, nowUtc, force: false);

            using var bucketCommand = connection.CreateCommand();
            bucketCommand.Transaction = transaction;
            bucketCommand.CommandText = @"INSERT INTO file_hash_seen_buckets (file_hash, bucket_date, count)
VALUES ($hash, $bucket, 1)
ON CONFLICT(file_hash, bucket_date) DO UPDATE SET count = count + 1;";
            var bucketHashParam = bucketCommand.CreateParameter();
            bucketHashParam.ParameterName = "$hash";
            bucketCommand.Parameters.Add(bucketHashParam);
            var bucketDateParam = bucketCommand.CreateParameter();
            bucketDateParam.ParameterName = "$bucket";
            bucketCommand.Parameters.Add(bucketDateParam);

            using var aggregateCommand = connection.CreateCommand();
            aggregateCommand.Transaction = transaction;
            aggregateCommand.CommandText = @"INSERT INTO file_hash_uid (file_hash, first_seen_at, last_seen_at, seen_count)
VALUES ($hash, $seen, $seen, 1)
ON CONFLICT(file_hash) DO UPDATE SET
    first_seen_at = MIN(file_hash_uid.first_seen_at, excluded.first_seen_at),
    last_seen_at = MAX(file_hash_uid.last_seen_at, excluded.last_seen_at),
    seen_count = file_hash_uid.seen_count + 1;";
            var aggregateHashParam = aggregateCommand.CreateParameter();
            aggregateHashParam.ParameterName = "$hash";
            aggregateCommand.Parameters.Add(aggregateHashParam);
            var aggregateSeenParam = aggregateCommand.CreateParameter();
            aggregateSeenParam.ParameterName = "$seen";
            aggregateCommand.Parameters.Add(aggregateSeenParam);

            foreach (var entry in buffer)
            {
                var timestamp = entry.SeenAtUtc.ToUniversalTime();
                bucketHashParam.Value = entry.FileHash;
                bucketDateParam.Value = timestamp.Date.ToString(BucketDateFormat, CultureInfo.InvariantCulture);
                await bucketCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                aggregateHashParam.Value = entry.FileHash;
                aggregateSeenParam.Value = timestamp.ToString("o", CultureInfo.InvariantCulture);
                await aggregateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (shouldVacuum)
            {
                ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                ExecuteNonQuery(connection, "VACUUM;");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush file usage buffer");
        }
        finally
        {
            buffer.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _queueCts.CancelAsync().ConfigureAwait(false);
        await _cleanupCts.CancelAsync().ConfigureAwait(false);
        _fileSeenChannel.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_queueWorker, _cleanupTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation during disposal
        }
        _queueCts.Dispose();
        _cleanupCts.Dispose();
    }

    private void RunMaintenance()
    {
        using var writeScope = _db.EnterWrite();
        using var connection = _db.Open();

        if (PerformRetentionCleanup(connection, null, DateTime.UtcNow, force: true))
        {
            ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            ExecuteNonQuery(connection, "VACUUM;");
        }
    }

}
