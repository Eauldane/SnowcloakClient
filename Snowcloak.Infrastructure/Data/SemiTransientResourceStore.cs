using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Snowcloak.Infrastructure.Data;

public sealed class SemiTransientResourceStore
{
    private const int GlobalScope = 0;
    private const int JobScope = 1;
    private readonly SqliteDatabase _db;

    public SemiTransientResourceStore(SqliteDatabase db)
    {
        _db = db;
    }

    public HashSet<string> Load(string characterKey, int objectKind, uint jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterKey);

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT game_path
FROM semi_transient_resources
WHERE character_key = $character
AND object_kind = $kind
AND ((scope = $globalScope AND job_id = 0) OR (scope = $jobScope AND job_id = $job))
ORDER BY game_path;";
        command.Parameters.AddWithValue("$character", characterKey);
        command.Parameters.AddWithValue("$kind", objectKind);
        command.Parameters.AddWithValue("$globalScope", GlobalScope);
        command.Parameters.AddWithValue("$jobScope", JobScope);
        command.Parameters.AddWithValue("$job", Convert.ToInt64(jobId, CultureInfo.InvariantCulture));

        HashSet<string> resources = new(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            resources.Add(reader.GetString(0));
        }

        return resources;
    }

    public void SavePlayerPaths(string characterKey, int objectKind, uint jobId, IEnumerable<string> gamePaths, DateTime seenAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterKey);
        ArgumentNullException.ThrowIfNull(gamePaths);

        var paths = NormalisePaths(gamePaths);
        if (paths.Count == 0)
        {
            return;
        }

        using var writeScope = _db.EnterWrite();
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var path in paths)
        {
            if (Exists(connection, transaction, characterKey, objectKind, GlobalScope, 0, path))
            {
                Touch(connection, transaction, characterKey, objectKind, GlobalScope, 0, path, seenAtUtc);
                continue;
            }

            if (ExistsOnOtherJob(connection, transaction, characterKey, objectKind, jobId, path))
            {
                DeleteJobScopedPath(connection, transaction, characterKey, objectKind, path);
                Upsert(connection, transaction, characterKey, objectKind, GlobalScope, 0, path, seenAtUtc);
                continue;
            }

            Upsert(connection, transaction, characterKey, objectKind, JobScope, jobId, path, seenAtUtc);
        }

        transaction.Commit();
    }

    public void SaveJobScopedPaths(string characterKey, int objectKind, uint jobId, IEnumerable<string> gamePaths, DateTime seenAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterKey);
        ArgumentNullException.ThrowIfNull(gamePaths);

        var paths = NormalisePaths(gamePaths);
        if (paths.Count == 0)
        {
            return;
        }

        using var writeScope = _db.EnterWrite();
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var path in paths)
        {
            Upsert(connection, transaction, characterKey, objectKind, JobScope, jobId, path, seenAtUtc);
        }

        transaction.Commit();
    }

    public int RemovePath(string characterKey, int objectKind, string gamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);

        using var writeScope = _db.EnterWrite();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"DELETE FROM semi_transient_resources
WHERE character_key = $character
AND object_kind = $kind
AND game_path = $path;";
        command.Parameters.AddWithValue("$character", characterKey);
        command.Parameters.AddWithValue("$kind", objectKind);
        command.Parameters.AddWithValue("$path", NormalisePath(gamePath));
        return command.ExecuteNonQuery();
    }

    public int RemovePaths(string characterKey, int objectKind, IEnumerable<string> gamePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterKey);
        ArgumentNullException.ThrowIfNull(gamePaths);

        var paths = NormalisePaths(gamePaths);
        if (paths.Count == 0)
        {
            return 0;
        }

        var removed = 0;
        using var writeScope = _db.EnterWrite();
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"DELETE FROM semi_transient_resources
WHERE character_key = $character
AND object_kind = $kind
AND game_path = $path;";
        command.Parameters.AddWithValue("$character", characterKey);
        command.Parameters.AddWithValue("$kind", objectKind);
        var pathParameter = command.CreateParameter();
        pathParameter.ParameterName = "$path";
        command.Parameters.Add(pathParameter);

        foreach (var path in paths)
        {
            pathParameter.Value = path;
            removed += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return removed;
    }

    public void ImportLegacy(
        string characterKey,
        int playerObjectKind,
        int petObjectKind,
        IEnumerable<string> globalPlayerPaths,
        IReadOnlyDictionary<uint, List<string>> playerJobPaths,
        IReadOnlyDictionary<uint, List<string>> petJobPaths,
        DateTime seenAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterKey);
        ArgumentNullException.ThrowIfNull(globalPlayerPaths);
        ArgumentNullException.ThrowIfNull(playerJobPaths);
        ArgumentNullException.ThrowIfNull(petJobPaths);

        using var writeScope = _db.EnterWrite();
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var path in NormalisePaths(globalPlayerPaths))
        {
            InsertIfMissing(connection, transaction, characterKey, playerObjectKind, GlobalScope, 0, path, seenAtUtc);
        }

        foreach (var (jobId, paths) in playerJobPaths)
        {
            foreach (var path in NormalisePaths(paths))
            {
                InsertIfMissing(connection, transaction, characterKey, playerObjectKind, JobScope, jobId, path, seenAtUtc);
            }
        }

        foreach (var (jobId, paths) in petJobPaths)
        {
            foreach (var path in NormalisePaths(paths))
            {
                InsertIfMissing(connection, transaction, characterKey, petObjectKind, JobScope, jobId, path, seenAtUtc);
            }
        }

        transaction.Commit();
    }

    public int PruneOlderThan(DateTime thresholdUtc)
    {
        using var writeScope = _db.EnterWrite();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"DELETE FROM semi_transient_resources
WHERE last_seen_at < $threshold;";
        command.Parameters.AddWithValue("$threshold", FormatTimestamp(thresholdUtc));
        return command.ExecuteNonQuery();
    }

    private static List<string> NormalisePaths(IEnumerable<string> gamePaths)
    {
        return gamePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalisePath)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalisePath(string gamePath)
    {
        return gamePath.Replace('\\', '/').ToLowerInvariant();
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction transaction, string characterKey, int objectKind, int scope, uint jobId, string gamePath)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT 1
FROM semi_transient_resources
WHERE character_key = $character
AND object_kind = $kind
AND scope = $scope
AND job_id = $job
AND game_path = $path
LIMIT 1;";
        command.Parameters.AddWithValue("$character", characterKey);
        command.Parameters.AddWithValue("$kind", objectKind);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$job", Convert.ToInt64(jobId, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$path", gamePath);
        return command.ExecuteScalar() != null;
    }

    private static bool ExistsOnOtherJob(SqliteConnection connection, SqliteTransaction transaction, string characterKey, int objectKind, uint jobId, string gamePath)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT 1
FROM semi_transient_resources
WHERE character_key = $character
AND object_kind = $kind
AND scope = $jobScope
AND job_id <> $job
AND game_path = $path
LIMIT 1;";
        command.Parameters.AddWithValue("$character", characterKey);
        command.Parameters.AddWithValue("$kind", objectKind);
        command.Parameters.AddWithValue("$jobScope", JobScope);
        command.Parameters.AddWithValue("$job", Convert.ToInt64(jobId, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$path", gamePath);
        return command.ExecuteScalar() != null;
    }

    private static void DeleteJobScopedPath(SqliteConnection connection, SqliteTransaction transaction, string characterKey, int objectKind, string gamePath)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"DELETE FROM semi_transient_resources
WHERE character_key = $character
AND object_kind = $kind
AND scope = $jobScope
AND game_path = $path;";
        command.Parameters.AddWithValue("$character", characterKey);
        command.Parameters.AddWithValue("$kind", objectKind);
        command.Parameters.AddWithValue("$jobScope", JobScope);
        command.Parameters.AddWithValue("$path", gamePath);
        command.ExecuteNonQuery();
    }

    private static void Touch(SqliteConnection connection, SqliteTransaction transaction, string characterKey, int objectKind, int scope, uint jobId, string gamePath, DateTime seenAtUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"UPDATE semi_transient_resources
SET last_seen_at = $seen,
    seen_count = seen_count + 1
WHERE character_key = $character
AND object_kind = $kind
AND scope = $scope
AND job_id = $job
AND game_path = $path;";
        command.Parameters.AddWithValue("$seen", FormatTimestamp(seenAtUtc));
        command.Parameters.AddWithValue("$character", characterKey);
        command.Parameters.AddWithValue("$kind", objectKind);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$job", Convert.ToInt64(jobId, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$path", gamePath);
        command.ExecuteNonQuery();
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, string characterKey, int objectKind, int scope, uint jobId, string gamePath, DateTime seenAtUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO semi_transient_resources (
    character_key,
    object_kind,
    scope,
    job_id,
    game_path,
    first_seen_at,
    last_seen_at,
    seen_count)
VALUES ($character, $kind, $scope, $job, $path, $seen, $seen, 1)
ON CONFLICT(character_key, object_kind, scope, job_id, game_path) DO UPDATE SET
    last_seen_at = excluded.last_seen_at,
    seen_count = seen_count + 1;";
        command.Parameters.AddWithValue("$character", characterKey);
        command.Parameters.AddWithValue("$kind", objectKind);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$job", Convert.ToInt64(jobId, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$path", gamePath);
        command.Parameters.AddWithValue("$seen", FormatTimestamp(seenAtUtc));
        command.ExecuteNonQuery();
    }

    private static void InsertIfMissing(SqliteConnection connection, SqliteTransaction transaction, string characterKey, int objectKind, int scope, uint jobId, string gamePath, DateTime seenAtUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT OR IGNORE INTO semi_transient_resources (
    character_key,
    object_kind,
    scope,
    job_id,
    game_path,
    first_seen_at,
    last_seen_at,
    seen_count)
VALUES ($character, $kind, $scope, $job, $path, $seen, $seen, 1);";
        command.Parameters.AddWithValue("$character", characterKey);
        command.Parameters.AddWithValue("$kind", objectKind);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$job", Convert.ToInt64(jobId, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$path", gamePath);
        command.Parameters.AddWithValue("$seen", FormatTimestamp(seenAtUtc));
        command.ExecuteNonQuery();
    }

    private static string FormatTimestamp(DateTime timestampUtc)
    {
        return timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
