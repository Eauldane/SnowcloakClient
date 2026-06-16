using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Snowcloak.Infrastructure.Data;

public sealed class SqliteDatabase
{
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    
    public SqliteDatabase(string databaseDirectory, ILogger<SqliteDatabase> logger)
    {
        _logger = logger;
        var databasePath = Path.Combine(databaseDirectory, "Snowcloak.sqlite");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();

        EnsureSchema();
    }

    public int SchemaVersion { get; private set; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyConnectionPragmas(connection);
        return connection;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        ApplyConnectionPragmas(connection);
        return connection;
    }

    public IDisposable EnterWrite()
    {
        _writeLock.Wait();
        return new Releaser(_writeLock);
    }

    public async Task<IDisposable> EnterWriteAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_writeLock);
    }

    private static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");
    }

    private void EnsureSchema()
    {
        using var writeScope = EnterWrite();
        using var connection = Open();

        Execute(connection, "PRAGMA journal_mode=WAL;");

        ApplyMigrations(connection);

        _logger.LogInformation("Database initialized! Schema version: {Version}", SchemaVersion);
    }

    private void ApplyMigrations(SqliteConnection connection)
    {
        int schemaVersion = GetUserVersion(connection);
        var vacuumAfterMigration = false;
        using var transaction = connection.BeginTransaction();
        if (schemaVersion == 0)
        {
            Execute(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_uid (
    file_hash TEXT NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    seen_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_hash));");

            Execute(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_seen_buckets (
    file_hash TEXT NOT NULL,
    bucket_date TEXT NOT NULL,
    count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_hash, bucket_date));");


            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_uid_last_seen ON file_hash_uid(last_seen_at DESC);");
            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_date ON file_hash_seen_buckets(bucket_date);");
            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_hash ON file_hash_seen_buckets(file_hash, bucket_date);");

            SetUserVersion(connection, transaction, 4);
            schemaVersion = 4;
        }

        if (schemaVersion == 1)
        {
            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_uid_hash ON file_hash_uid(file_hash);");
            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_events_hash_uid ON file_hash_seen_events(file_hash, uid, seen_at DESC);");
            var fileHashUidColumns = GetTableColumns(connection, transaction, "file_hash_uid");
            EnsureColumnExists(connection, transaction, "file_hash_uid", fileHashUidColumns, "first_seen_at", "TEXT");
            EnsureColumnExists(connection, transaction, "file_hash_uid", fileHashUidColumns, "last_seen_at", "TEXT");
            EnsureColumnExists(connection, transaction, "file_hash_uid", fileHashUidColumns, "seen_count", "INTEGER");
            Execute(connection, transaction, @"UPDATE file_hash_uid SET seen_count = 1 WHERE seen_count IS NULL;");

            SetUserVersion(connection, transaction, 2);
            schemaVersion = 2;
        }

        if (schemaVersion <= 2)
        {
            Execute(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_seen_buckets (
    file_hash TEXT NOT NULL,
    uid TEXT,
    bucket_date TEXT NOT NULL,
    count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_hash, uid, bucket_date));");

            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_date ON file_hash_seen_buckets(bucket_date);");
            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_hash ON file_hash_seen_buckets(file_hash, bucket_date);");

            Execute(connection, transaction, @"INSERT INTO file_hash_seen_buckets (file_hash, uid, bucket_date, count)
SELECT file_hash, uid, strftime('%Y-%m-%d', seen_at) AS bucket_date, COUNT(*)
FROM file_hash_seen_events
GROUP BY file_hash, uid, bucket_date
ON CONFLICT(file_hash, uid, bucket_date) DO UPDATE SET count = file_hash_seen_buckets.count + excluded.count;");

            Execute(connection, transaction, @"DROP TABLE IF EXISTS file_hash_seen_events;");

            Execute(connection, transaction, "DELETE FROM file_hash_uid;");
            Execute(connection, transaction, @"INSERT INTO file_hash_uid (uid, file_hash, first_seen_at, last_seen_at, seen_count)
SELECT uid, file_hash, MIN(bucket_date || 'T00:00:00Z'), MAX(bucket_date || 'T00:00:00Z'), SUM(count)
FROM file_hash_seen_buckets
GROUP BY uid, file_hash;");

            SetUserVersion(connection, transaction, 3);
            schemaVersion = 3;
        }

        if (schemaVersion <= 3)
        {
            Execute(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_seen_buckets_new (
    file_hash TEXT NOT NULL,
    bucket_date TEXT NOT NULL,
    count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_hash, bucket_date));");

            Execute(connection, transaction, @"INSERT INTO file_hash_seen_buckets_new (file_hash, bucket_date, count)
SELECT file_hash, bucket_date, SUM(count)
FROM file_hash_seen_buckets
GROUP BY file_hash, bucket_date
ON CONFLICT(file_hash, bucket_date) DO UPDATE SET count = file_hash_seen_buckets_new.count + excluded.count;");

            Execute(connection, transaction, @"DROP TABLE IF EXISTS file_hash_seen_buckets;");
            Execute(connection, transaction, @"ALTER TABLE file_hash_seen_buckets_new RENAME TO file_hash_seen_buckets;");

            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_date ON file_hash_seen_buckets(bucket_date);");
            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_seen_buckets_hash ON file_hash_seen_buckets(file_hash, bucket_date);");

            Execute(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_hash_uid_new (
    file_hash TEXT NOT NULL,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    seen_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file_hash));");

            Execute(connection, transaction, @"INSERT INTO file_hash_uid_new (file_hash, first_seen_at, last_seen_at, seen_count)
SELECT file_hash, MIN(bucket_date || 'T00:00:00Z'), MAX(bucket_date || 'T00:00:00Z'), SUM(count)
FROM file_hash_seen_buckets
GROUP BY file_hash;");

            Execute(connection, transaction, @"DROP TABLE IF EXISTS file_hash_uid;");
            Execute(connection, transaction, @"ALTER TABLE file_hash_uid_new RENAME TO file_hash_uid;");

            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_hash_uid_last_seen ON file_hash_uid(last_seen_at DESC);");

            SetUserVersion(connection, transaction, 4);
            vacuumAfterMigration = true;
        }

        if (schemaVersion <= 4)
        {
            Execute(connection, transaction, @"CREATE TABLE IF NOT EXISTS file_cache (
    prefixed_path TEXT NOT NULL PRIMARY KEY,
    hash TEXT NOT NULL,
    last_modified_ticks TEXT NOT NULL,
    size INTEGER NOT NULL DEFAULT -1,
    compressed_size INTEGER NOT NULL DEFAULT -1
);");

            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_file_cache_hash ON file_cache(hash);");

            SetUserVersion(connection, transaction, 5);
        }

        if (schemaVersion <= 5)
        {
            Execute(connection, transaction, @"CREATE TABLE IF NOT EXISTS state_documents (
    document_name TEXT NOT NULL PRIMARY KEY,
    payload TEXT NOT NULL,
    updated_at TEXT NOT NULL
);");

            Execute(connection, transaction, @"CREATE TABLE IF NOT EXISTS state_document_backups (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    document_name TEXT NOT NULL,
    created_at TEXT NOT NULL,
    payload TEXT NOT NULL
);");

            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_state_document_backups_document ON state_document_backups(document_name, id DESC);");

            SetUserVersion(connection, transaction, 6);
        }

        if (schemaVersion <= 6)
        {
            Execute(connection, transaction, @"CREATE TABLE IF NOT EXISTS semi_transient_resources (
    character_key TEXT NOT NULL,
    object_kind INTEGER NOT NULL,
    scope INTEGER NOT NULL,
    job_id INTEGER NOT NULL,
    game_path TEXT NOT NULL COLLATE NOCASE,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    seen_count INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (character_key, object_kind, scope, job_id, game_path));");

            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_semi_transient_resources_character ON semi_transient_resources(character_key, object_kind, scope, job_id);");
            Execute(connection, transaction,
                @"CREATE INDEX IF NOT EXISTS idx_semi_transient_resources_last_seen ON semi_transient_resources(last_seen_at);");

            SetUserVersion(connection, transaction, 7);
        }

        if (schemaVersion <= 7)
        {
            Execute(connection, transaction, @"CREATE TABLE IF NOT EXISTS usage_statistics (
    metric TEXT NOT NULL PRIMARY KEY,
    value INTEGER NOT NULL DEFAULT 0
);");

            SetUserVersion(connection, transaction, 8);
        }

        transaction.Commit();
        SchemaVersion = GetUserVersion(connection);

        if (vacuumAfterMigration)
        {
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, "VACUUM;");
        }
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version = " + version + ";";
        command.ExecuteNonQuery();
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
        Execute(connection, transaction,
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
        existingColumns.Add(columnName);
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            semaphore.Release();
        }
    }
}
