using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Snowcloak.Infrastructure.Data;
using Snowcloak.Services;

namespace Snowcloak.FileCache;

public sealed class FileCacheIndex
{
    private readonly ILogger<FileCacheIndex> _logger;
    private readonly SqliteDatabase _db;

    public FileCacheIndex(ILogger<FileCacheIndex> logger, SqliteDatabase db)
    {
        _logger = logger;
        _db = db;
    }

    public List<FileCacheEntity> LoadAll()
    {
        List<FileCacheEntity> result = [];

        try
        {
            using var connection = _db.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT hash, prefixed_path, last_modified_ticks, size, compressed_size FROM file_cache;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var hash = reader.GetString(0);
                var path = reader.GetString(1);
                var ticks = reader.GetString(2);
                var size = reader.IsDBNull(3) ? -1 : reader.GetInt64(3);
                var compressedSize = reader.IsDBNull(4) ? -1 : reader.GetInt64(4);
                result.Add(new FileCacheEntity(hash, path, ticks, size, compressedSize));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load file cache index");
        }

        return result;
    }

    public void Upsert(FileCacheEntity entity)
    {
        using (_db.EnterWrite())
        {
            try
            {
                using var connection = _db.Open();
                using var command = CreateUpsertCommand(connection, null);
                BindAndExecute(command, entity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist file cache entry {path}", entity.PrefixedFilePath);
            }
        }
    }

    public void UpsertMany(IReadOnlyCollection<FileCacheEntity> entities)
    {
        if (entities.Count == 0) return;

        using (_db.EnterWrite())
        {
            try
            {
                using var connection = _db.Open();
                using var transaction = connection.BeginTransaction();
                try
                {
                    using var command = CreateUpsertCommand(connection, transaction);
                    foreach (var entity in entities)
                    {
                        BindAndExecute(command, entity);
                    }
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist {count} file cache entries", entities.Count);
            }
        }
    }

    public void ReplaceAll(IReadOnlyCollection<FileCacheEntity> entities)
    {
        using (_db.EnterWrite())
        {
            try
            {
                using var connection = _db.Open();
                using var transaction = connection.BeginTransaction();
                try
                {
                    using (var delete = connection.CreateCommand())
                    {
                        delete.Transaction = transaction;
                        delete.CommandText = "DELETE FROM file_cache;";
                        delete.ExecuteNonQuery();
                    }

                    using var command = CreateUpsertCommand(connection, transaction);
                    foreach (var entity in entities)
                    {
                        BindAndExecute(command, entity);
                    }

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write out file cache index");
            }
        }
    }

    public void Remove(string prefixedFilePath)
    {
        using (_db.EnterWrite())
        {
            try
            {
                using var connection = _db.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM file_cache WHERE prefixed_path = $path;";
                command.Parameters.AddWithValue("$path", prefixedFilePath);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove file cache entry {path}", prefixedFilePath);
            }
        }
    }

    private static SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO file_cache (prefixed_path, hash, last_modified_ticks, size, compressed_size)
VALUES ($path, $hash, $ticks, $size, $compressed)
ON CONFLICT(prefixed_path) DO UPDATE SET
    hash = excluded.hash,
    last_modified_ticks = excluded.last_modified_ticks,
    size = excluded.size,
    compressed_size = excluded.compressed_size;";
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$hash", SqliteType.Text);
        command.Parameters.Add("$ticks", SqliteType.Text);
        command.Parameters.Add("$size", SqliteType.Integer);
        command.Parameters.Add("$compressed", SqliteType.Integer);
        return command;
    }

    private static void BindAndExecute(SqliteCommand command, FileCacheEntity entity)
    {
        command.Parameters["$path"].Value = entity.PrefixedFilePath;
        command.Parameters["$hash"].Value = entity.Hash;
        command.Parameters["$ticks"].Value = entity.LastModifiedDateTicks;
        command.Parameters["$size"].Value = entity.Size ?? -1;
        command.Parameters["$compressed"].Value = entity.CompressedSize ?? -1;
        command.ExecuteNonQuery();
    }
}
