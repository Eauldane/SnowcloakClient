using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;


namespace Snowcloak.Services;

public class DatabaseService : IAsyncDisposable
{
    private readonly CapabilityRegistry _capabilityRegistry;
    private readonly SnowcloakConfigService _configService;
    private readonly ILogger<DatabaseService> _logger;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private float _clientDBVersion;
    private readonly object _cleanupLock = new();
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


    public DatabaseService(CapabilityRegistry capabilityRegistry, SnowcloakConfigService configService,
        ILogger<DatabaseService> logger)
    {
        _capabilityRegistry = capabilityRegistry;
        _logger = logger;
        _configService = configService;
        _databasePath = Path.Combine(configService.ConfigurationDirectory, "Snowcloak.sqlite");
        var connStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared,
        };
        _connectionString = connStringBuilder.ToString();

        InitDB();
        _capabilityRegistry.RegisterCapability("ClientDB", _clientDBVersion);
        _cleanupTask = Task.Run(() => PeriodicCleanupAsync(_cleanupCts.Token));
    }

    private void InitDB()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, "PRAGMA synchronous=NORMAL;");
        ExecuteNonQuery(connection, "PRAGMA foreign_keys=ON;");

        ApplyMigrations(connection);
        if (PerformRetentionCleanup(connection, null, DateTime.UtcNow, force: true))
        {
            ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            ExecuteNonQuery(connection, "VACUUM;");
        }

        _logger.LogInformation("Database initialized! Schema version: {version}", _clientDBVersion);
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version = " + version + ";";
        command.ExecuteNonQuery();
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
        _ = EnqueueFileSeen(uid, fileHash, seenAtUtc);
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
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

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
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
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
                
                int deletedBuckets;
                using (var deleteBuckets = connection.CreateCommand())
                {
                    deleteBuckets.Transaction = workingTransaction;
                    deleteBuckets.CommandText =
                        @"DELETE FROM file_hash_seen_buckets WHERE bucket_date < $threshold;";
                    deleteBuckets.Parameters.AddWithValue("$threshold", thresholdDate);
                    deletedBuckets = deleteBuckets.ExecuteNonQuery();
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
                return deletedBuckets > 0;
                
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

    public void ApplyMigrations(SqliteConnection connection)
    {
        int schemaVersion = GetUserVersion(connection);
        var vacuumAfterMigration = false;
        using var transaction = connection.BeginTransaction();
        if (schemaVersion == 0)
        {
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_uid (
    file_hash TEXT NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    seen_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_hash));");

            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_seen_buckets (
    file_hash TEXT NOT NULL,
    bucket_date TEXT NOT NULL,
    count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_hash, bucket_date));");


            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_uid_last_seen ON file_hash_uid(last_seen_at DESC);");
            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_date ON file_hash_seen_buckets(bucket_date);");
            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_hash ON file_hash_seen_buckets(file_hash, bucket_date);");

            SetUserVersion(connection, transaction, 4);
            schemaVersion = 4;
        }

        if (schemaVersion == 1)
        {
                           ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_uid_hash ON file_hash_uid(file_hash);");
            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_events_hash_uid ON file_hash_seen_events(file_hash, uid, seen_at DESC);");
            var fileHashUidColumns = GetTableColumns(connection, transaction, "file_hash_uid");
            EnsureColumnExists(connection, transaction, "file_hash_uid", fileHashUidColumns, "first_seen_at", "TEXT");
            EnsureColumnExists(connection, transaction, "file_hash_uid", fileHashUidColumns, "last_seen_at", "TEXT");
            EnsureColumnExists(connection, transaction, "file_hash_uid", fileHashUidColumns, "seen_count", "INTEGER");
            ExecuteNonQuery(connection, transaction, @"UPDATE file_hash_uid SET seen_count = 1 WHERE seen_count IS NULL;");

            SetUserVersion(connection, transaction, 2);
            schemaVersion = 2;
        }

        if (schemaVersion <= 2)
        {
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_seen_buckets (
    file_hash TEXT NOT NULL,
    uid TEXT,
    bucket_date TEXT NOT NULL,
    count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_hash, uid, bucket_date));");

            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_date ON file_hash_seen_buckets(bucket_date);");
            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_hash ON file_hash_seen_buckets(file_hash, bucket_date);");

            ExecuteNonQuery(connection, transaction, @"INSERT INTO file_hash_seen_buckets (file_hash, uid, bucket_date, count)
SELECT file_hash, uid, strftime('%Y-%m-%d', seen_at) AS bucket_date, COUNT(*)
FROM file_hash_seen_events
GROUP BY file_hash, uid, bucket_date
ON CONFLICT(file_hash, uid, bucket_date) DO UPDATE SET count = file_hash_seen_buckets.count + excluded.count;");

            ExecuteNonQuery(connection, transaction, @"DROP TABLE IF EXISTS file_hash_seen_events;");

            ExecuteNonQuery(connection, transaction, "DELETE FROM file_hash_uid;");
            ExecuteNonQuery(connection, transaction, @"INSERT INTO file_hash_uid (uid, file_hash, first_seen_at, last_seen_at, seen_count)
SELECT uid, file_hash, MIN(bucket_date || 'T00:00:00Z'), MAX(bucket_date || 'T00:00:00Z'), SUM(count)
FROM file_hash_seen_buckets
GROUP BY uid, file_hash;");

            SetUserVersion(connection, transaction, 3);
            schemaVersion = 3;
        }
        
                if (schemaVersion <= 3)
        {
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_seen_buckets_new (
    file_hash TEXT NOT NULL,
    bucket_date TEXT NOT NULL,
    count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_hash, bucket_date));");

            ExecuteNonQuery(connection, transaction, @"INSERT INTO file_hash_seen_buckets_new (file_hash, bucket_date, count)
SELECT file_hash, bucket_date, SUM(count)
FROM file_hash_seen_buckets
GROUP BY file_hash, bucket_date
ON CONFLICT(file_hash, bucket_date) DO UPDATE SET count = file_hash_seen_buckets_new.count + excluded.count;");

            ExecuteNonQuery(connection, transaction, @"DROP TABLE IF EXISTS file_hash_seen_buckets;");
            ExecuteNonQuery(connection, transaction, @"ALTER TABLE file_hash_seen_buckets_new RENAME TO file_hash_seen_buckets;");

            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_date ON file_hash_seen_buckets(bucket_date);");
            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_hash ON file_hash_seen_buckets(file_hash, bucket_date);");

            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_uid_new (
    file_hash TEXT NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    seen_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_hash));");

            ExecuteNonQuery(connection, transaction, @"INSERT INTO file_hash_uid_new (file_hash, first_seen_at, last_seen_at, seen_count)
SELECT file_hash, MIN(bucket_date || 'T00:00:00Z'), MAX(bucket_date || 'T00:00:00Z'), SUM(count)
FROM file_hash_seen_buckets
GROUP BY file_hash;");

            ExecuteNonQuery(connection, transaction, @"DROP TABLE IF EXISTS file_hash_uid;");
            ExecuteNonQuery(connection, transaction, @"ALTER TABLE file_hash_uid_new RENAME TO file_hash_uid;");

            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_uid_last_seen ON file_hash_uid(last_seen_at DESC);");

            SetUserVersion(connection, transaction, 4);
            schemaVersion = 4;
            vacuumAfterMigration = true;
        }


        transaction.Commit();
        _clientDBVersion = GetUserVersion(connection);

        if (vacuumAfterMigration)
        {
            ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            ExecuteNonQuery(connection, "VACUUM;");
        }

    }
    
    private static HashSet<string> GetTableColumns(SqliteConnection connection, SqliteTransaction transaction, string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName});";
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static void EnsureColumnExists(SqliteConnection connection, SqliteTransaction transaction, string tableName,
        HashSet<string> existingColumns, string columnName, string columnDefinition)
    {
        if (existingColumns.Contains(columnName)) return;
        ExecuteNonQuery(connection, transaction,
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
        existingColumns.Add(columnName);
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

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var readTask = _fileSeenChannel.Reader.ReadAsync(cancellationToken).AsTask();
                    var timerTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
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
                    }
                    else if (buffer.Count > 0)
                    {
                        await FlushFileSeenBufferAsync(buffer, cancellationToken).ConfigureAwait(false);
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

    private async Task FlushFileSeenBufferAsync(List<FileSeenEntry> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Count == 0) return;

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var nowUtc = DateTime.UtcNow;
            bool shouldVacuum = PerformRetentionCleanup(connection, transaction, nowUtc, force: false);

            await using var bucketCommand = connection.CreateCommand();
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

            await using var aggregateCommand = connection.CreateCommand();
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
                bucketCommand.ExecuteNonQuery();

                aggregateHashParam.Value = entry.FileHash;
                aggregateSeenParam.Value = timestamp.ToString("o", CultureInfo.InvariantCulture);
                aggregateCommand.ExecuteNonQuery();
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
        _queueCts.Cancel();
        _fileSeenChannel.Writer.TryComplete();
        try
        {
            await _queueWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation during disposal
        }
        _queueCts.Dispose();
    }

    private void RunMaintenance()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        if (PerformRetentionCleanup(connection, null, DateTime.UtcNow, force: true))
        {
            ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            ExecuteNonQuery(connection, "VACUUM;");
        }
    }

    public void Dispose()
    {
        _cleanupCts.Cancel();
        try
        {
            _cleanupTask.Wait();
        }
        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
        {
        }
        catch (TaskCanceledException)
        {
        }

        _cleanupCts.Dispose();
    }
    
    
}

