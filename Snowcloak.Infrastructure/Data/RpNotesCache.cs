using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Snowcloak.Infrastructure.Data;

public sealed class RpNotesCache
{
    private readonly string _connection;
    private readonly string _path;
    private bool _recoveryCopied;
    public RpNotesCache(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "DetailedNotes.sqlite");
        _connection = new SqliteConnectionStringBuilder { DataSource = _path,
            Pooling = false }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version > 1) throw new InvalidDataException("Detailed notes cache requires a newer client.");
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS notes_state (scope TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY(scope,key)); PRAGMA user_version=1;";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;"; cmd.ExecuteNonQuery();
        return db;
    }
    public Dictionary<string, T> Read<T>(string scope)
    {
        try {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT key,value FROM notes_state WHERE scope=$scope;";
        cmd.Parameters.AddWithValue("$scope", scope);
        using var reader = cmd.ExecuteReader();
        var values = new Dictionary<string, T>(StringComparer.Ordinal);
        while (reader.Read()) values.Add(reader.GetString(0), JsonSerializer.Deserialize<T>(reader.GetString(1))
            ?? throw new InvalidDataException("Invalid detailed notes cache."));
        return values;
        } catch (Exception error) when (error is JsonException or InvalidDataException) {
            if (!_recoveryCopied) {
                File.Copy(_path, _path + ".recovery-" + Guid.NewGuid().ToString("N"));
                _recoveryCopied = true;
            }
            throw new InvalidDataException("Detailed notes cache unreadable; a recovery copy was kept.", error);
        }
    }
    public string[] Scopes()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT scope FROM notes_state ORDER BY scope;";
        using var reader = cmd.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result.ToArray();
    }
    public void Save<T>(string scope, string key, T value)
    {
        using var db = Open(); using var transaction = db.BeginTransaction(); using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO notes_state(scope,key,value) VALUES($scope,$key,$value) ON CONFLICT(scope,key) DO UPDATE SET value=excluded.value;";
        cmd.Parameters.AddWithValue("$scope", scope); cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value)); cmd.ExecuteNonQuery(); transaction.Commit();
    }
    public void Delete(string scope)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM notes_state WHERE scope=$scope;"; cmd.Parameters.AddWithValue("$scope", scope); cmd.ExecuteNonQuery();
    }
}
