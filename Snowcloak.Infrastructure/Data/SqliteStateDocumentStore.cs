using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Snowcloak.Infrastructure.Data;

public sealed class SqliteStateDocumentStore
{
    private const int MaxBackups = 10;
    private readonly SqliteDatabase _db;

    public SqliteStateDocumentStore(SqliteDatabase db)
    {
        _db = db;
    }

    public string? Load(string documentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT payload FROM state_documents WHERE document_name = $name;";
        command.Parameters.AddWithValue("$name", documentName);
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyList<string> LoadBackups(string documentName, int limit = MaxBackups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT payload
FROM state_document_backups
WHERE document_name = $name
ORDER BY id DESC
LIMIT $limit;";
        command.Parameters.AddWithValue("$name", documentName);
        command.Parameters.AddWithValue("$limit", limit);

        List<string> payloads = [];
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    public void Save(string documentName, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        ArgumentNullException.ThrowIfNull(payload);

        using (_db.EnterWrite())
        {
            using var connection = _db.Open();
            using var transaction = connection.BeginTransaction();

            var existingPayload = LoadCurrentPayload(connection, transaction, documentName);
            if (string.Equals(existingPayload, payload, StringComparison.Ordinal))
            {
                transaction.Commit();
                return;
            }

            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            if (existingPayload != null)
            {
                using var backup = connection.CreateCommand();
                backup.Transaction = transaction;
                backup.CommandText = @"INSERT INTO state_document_backups (document_name, created_at, payload)
VALUES ($name, $created, $payload);";
                backup.Parameters.AddWithValue("$name", documentName);
                backup.Parameters.AddWithValue("$created", now);
                backup.Parameters.AddWithValue("$payload", existingPayload);
                backup.ExecuteNonQuery();
            }

            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = @"INSERT INTO state_documents (document_name, payload, updated_at)
VALUES ($name, $payload, $updated)
ON CONFLICT(document_name) DO UPDATE SET
    payload = excluded.payload,
    updated_at = excluded.updated_at;";
                upsert.Parameters.AddWithValue("$name", documentName);
                upsert.Parameters.AddWithValue("$payload", payload);
                upsert.Parameters.AddWithValue("$updated", now);
                upsert.ExecuteNonQuery();
            }

            using (var prune = connection.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText = @"DELETE FROM state_document_backups
WHERE document_name = $name
AND id NOT IN (
    SELECT id
    FROM state_document_backups
    WHERE document_name = $name
    ORDER BY id DESC
    LIMIT $limit
);";
                prune.Parameters.AddWithValue("$name", documentName);
                prune.Parameters.AddWithValue("$limit", MaxBackups);
                prune.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private static string? LoadCurrentPayload(SqliteConnection connection, SqliteTransaction transaction, string documentName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"SELECT payload FROM state_documents WHERE document_name = $name;";
        command.Parameters.AddWithValue("$name", documentName);
        return command.ExecuteScalar() as string;
    }
}
