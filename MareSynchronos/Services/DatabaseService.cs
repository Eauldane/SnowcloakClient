using MareSynchronos.MareConfiguration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace MareSynchronos.Services;

public class DatabaseService
{
    private readonly CapabilityRegistry _capabilityRegistry;
    private readonly MareConfigService _configService;
    private readonly ILogger<DatabaseService> _logger;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private float _clientDBVersion;
    
    public DatabaseService(CapabilityRegistry capabilityRegistry, MareConfigService configService,  ILogger<DatabaseService> logger)
    {
        _capabilityRegistry = capabilityRegistry;
        _logger = logger;
        _configService = configService;
        _databasePath = Path.Combine(configService.ConfigurationDirectory, "Snowcloak.sqlite");
        var connStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
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
        command.CommandText = "PRAGMA user_version = "+version+";";
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

        var timestamp = seenAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var usageCommand = connection.CreateCommand())
            {
                usageCommand.Transaction = transaction;
                usageCommand.CommandText = @"INSERT INTO file_hash_uid (uid, file_hash, first_seen_at, last_seen_at, seen_count)
VALUES ($uid, $hash, $seen, $seen, 1)
ON CONFLICT(uid, file_hash) DO UPDATE SET
    last_seen_at = excluded.last_seen_at,
    seen_count = file_hash_uid.seen_count + 1;";
                usageCommand.Parameters.AddWithValue("$uid", uid);
                usageCommand.Parameters.AddWithValue("$hash", fileHash);
                usageCommand.Parameters.AddWithValue("$seen", timestamp);
                usageCommand.ExecuteNonQuery();
            }

            using (var eventCommand = connection.CreateCommand())
            {
                eventCommand.Transaction = transaction;
                eventCommand.CommandText = @"INSERT INTO file_hash_seen_events (file_hash, uid, seen_at)
VALUES ($hash, $uid, $seen);";
                eventCommand.Parameters.AddWithValue("$hash", fileHash);
                eventCommand.Parameters.AddWithValue("$uid", uid);
                eventCommand.Parameters.AddWithValue("$seen", timestamp);
                eventCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record file usage for {uid} and {hash}", uid, fileHash);
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
             
        }

        transaction.Commit();
        _clientDBVersion = GetUserVersion(connection);
        
    }
}
