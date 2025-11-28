using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.SnowcloakConfiguration;
using System.Globalization;

namespace Snowcloak.Services;

public class DatabaseService
{
    private readonly CapabilityRegistry _capabilityRegistry;
    private readonly SnowcloakConfigService _configService;
    private readonly ILogger<DatabaseService> _logger;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private float _clientDBVersion;
    private readonly object _cleanupLock = new();
    private DateTime _lastCleanupUtc = DateTime.MinValue;
    public static readonly TimeSpan UsageRetentionPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    public readonly record struct FileUsageStatistics(int SeenCount, DateTime? LastSeenUtc);

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
        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(fileHash)) return;


        var normalizedHash = fileHash.ToUpperInvariant();
        var nowUtc = DateTime.UtcNow;
        var retentionThreshold = nowUtc - UsageRetentionPeriod;
        var timestamp = seenAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        var thresholdTimestamp = retentionThreshold.ToString("o", CultureInfo.InvariantCulture);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            bool shouldVacuum = PerformRetentionCleanup(connection, transaction, nowUtc, force: false);

            using (var eventCommand = connection.CreateCommand())
            {
                eventCommand.Transaction = transaction;
                eventCommand.CommandText = @"INSERT INTO file_hash_seen_events (file_hash, uid, seen_at)
VALUES ($hash, $uid, $seen);";
                eventCommand.Parameters.AddWithValue("$hash", normalizedHash);
                eventCommand.Parameters.AddWithValue("$uid", uid);
                eventCommand.Parameters.AddWithValue("$seen", timestamp);
                eventCommand.ExecuteNonQuery();
            }

            using (var aggregateCommand = connection.CreateCommand())
            {
                aggregateCommand.Transaction = transaction;
                aggregateCommand.CommandText = @"WITH stats AS (
    SELECT MIN(seen_at) AS first_seen_at,
           MAX(seen_at) AS last_seen_at,
           COUNT(*) AS seen_count
    FROM file_hash_seen_events
    WHERE file_hash = $hash AND uid = $uid AND seen_at >= $threshold)
INSERT INTO file_hash_uid (uid, file_hash, first_seen_at, last_seen_at, seen_count)
SELECT $uid, $hash, stats.first_seen_at, stats.last_seen_at, stats.seen_count
FROM stats
WHERE stats.seen_count > 0
ON CONFLICT(uid, file_hash) DO UPDATE SET
    first_seen_at = excluded.first_seen_at,
    last_seen_at = excluded.last_seen_at,
    seen_count = excluded.seen_count;";
                aggregateCommand.Parameters.AddWithValue("$uid", uid);
                aggregateCommand.Parameters.AddWithValue("$hash", normalizedHash);
                aggregateCommand.Parameters.AddWithValue("$threshold", thresholdTimestamp);
                aggregateCommand.ExecuteNonQuery();
            }

            transaction.Commit();

            if (shouldVacuum)
            {
                ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                ExecuteNonQuery(connection, "VACUUM;");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record file usage for {uid} and {hash}", uid, normalizedHash);
        }
    }

    public IReadOnlyDictionary<string, FileUsageStatistics> GetAggregatedFileUsage()
    {
        Dictionary<string, FileUsageStatistics> usage = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            if (PerformRetentionCleanup(connection, null, DateTime.UtcNow, force: false))
            {
                ExecuteNonQuery(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                ExecuteNonQuery(connection, "VACUUM;");
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT file_hash, SUM(seen_count) AS total_count, MAX(last_seen_at) AS last_seen
FROM file_hash_uid
GROUP BY file_hash;";

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

            using (var deleteEvents = connection.CreateCommand())
            {
                deleteEvents.Transaction = transaction;
                deleteEvents.CommandText = @"DELETE FROM file_hash_seen_events WHERE file_hash = $hash;";
                deleteEvents.Parameters.AddWithValue("$hash", normalizedHash);
                deleteEvents.ExecuteNonQuery();
            }

            using (var deleteUsage = connection.CreateCommand())
            {
                deleteUsage.Transaction = transaction;
                deleteUsage.CommandText = @"DELETE FROM file_hash_uid WHERE file_hash = $hash;";
                deleteUsage.Parameters.AddWithValue("$hash", normalizedHash);
                deleteUsage.ExecuteNonQuery();
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
                var thresholdTimestamp = threshold.ToString("o", CultureInfo.InvariantCulture);

                int deletedEvents;
                using (var deleteEvents = connection.CreateCommand())
                {
                    deleteEvents.Transaction = workingTransaction;
                    deleteEvents.CommandText = @"DELETE FROM file_hash_seen_events WHERE seen_at < $threshold;";
                    deleteEvents.Parameters.AddWithValue("$threshold", thresholdTimestamp);
                    deletedEvents = deleteEvents.ExecuteNonQuery();
                }

                ExecuteNonQuery(connection, workingTransaction, "DELETE FROM file_hash_uid;");

                using (var rebuildCommand = connection.CreateCommand())
                {
                    rebuildCommand.Transaction = workingTransaction;
                    rebuildCommand.CommandText =
                        @"INSERT INTO file_hash_uid (uid, file_hash, first_seen_at, last_seen_at, seen_count)
SELECT uid, file_hash, MIN(seen_at), MAX(seen_at), COUNT(*)
FROM file_hash_seen_events
GROUP BY uid, file_hash;";
                    rebuildCommand.ExecuteNonQuery();
                }

                if (ownTransaction)
                {
                    workingTransaction.Commit();
                }

                _lastCleanupUtc = nowUtc;
                return deletedEvents > 0;
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
        using var transaction = connection.BeginTransaction();
        if (schemaVersion == 0)
        {
            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_uid (
    uid TEXT NOT NULL,
    file_hash TEXT NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    seen_count INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (uid, file_hash));");

            ExecuteNonQuery(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_seen_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_hash TEXT NOT NULL,
    uid TEXT,
    seen_at TEXT NOT NULL);");

            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_events ON file_hash_seen_events(file_hash, seen_at DESC);");
            ExecuteNonQuery(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_uid_last_seen ON file_hash_uid(last_seen_at DESC);");
            SetUserVersion(connection, transaction, 1);
            schemaVersion = 1;
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
        }

        transaction.Commit();
        _clientDBVersion = GetUserVersion(connection);

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
}

