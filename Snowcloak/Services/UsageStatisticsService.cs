using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Snowcloak.Infrastructure.Data;
using System.Threading;

namespace Snowcloak.Services;

public sealed partial class UsageStatisticsService
{
    private const string DownloadedBytesMetric = "downloaded_bytes";
    private const string UploadedBytesMetric = "uploaded_bytes";
    private const string AppliedDataBytesMetric = "applied_data_bytes";
    private const string ViewedTrianglesMetric = "viewed_triangles";
    private const string ViewedVramBytesMetric = "viewed_vram_bytes";

    private readonly SqliteDatabase _db;
    private readonly ILogger<UsageStatisticsService> _logger;
    private long _sessionDownloadedBytes;
    private long _sessionUploadedBytes;
    private long _sessionAppliedDataBytes;
    private long _sessionViewedTriangles;
    private long _sessionViewedVramBytes;

    public UsageStatisticsService(SqliteDatabase db, ILogger<UsageStatisticsService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void RecordDownloadedBytes(long bytes)
    {
        if (bytes <= 0) return;

        Interlocked.Add(ref _sessionDownloadedBytes, bytes);
        AddMetric(DownloadedBytesMetric, bytes);
    }

    public void RecordUploadedBytes(long bytes)
    {
        if (bytes <= 0) return;

        Interlocked.Add(ref _sessionUploadedBytes, bytes);
        AddMetric(UploadedBytesMetric, bytes);
    }

    public void RecordAppliedLoad(long vramBytes, long triangleCount, long dataBytes)
    {
        var positiveVramBytes = Math.Max(0, vramBytes);
        var positiveTriangleCount = Math.Max(0, triangleCount);
        var positiveDataBytes = Math.Max(0, dataBytes);

        if (positiveVramBytes == 0 && positiveTriangleCount == 0 && positiveDataBytes == 0)
        {
            return;
        }

        Interlocked.Add(ref _sessionViewedVramBytes, positiveVramBytes);
        Interlocked.Add(ref _sessionViewedTriangles, positiveTriangleCount);
        Interlocked.Add(ref _sessionAppliedDataBytes, positiveDataBytes);
        AddMetrics(
            (ViewedVramBytesMetric, positiveVramBytes),
            (ViewedTrianglesMetric, positiveTriangleCount),
            (AppliedDataBytesMetric, positiveDataBytes));
    }

    public UsageStatisticsSnapshot GetSnapshot()
    {
        var lifetime = LoadLifetimeTotals();
        var session = new UsageStatisticsTotals(
            Interlocked.Read(ref _sessionDownloadedBytes),
            Interlocked.Read(ref _sessionUploadedBytes),
            Interlocked.Read(ref _sessionAppliedDataBytes),
            Interlocked.Read(ref _sessionViewedTriangles),
            Interlocked.Read(ref _sessionViewedVramBytes));

        return new UsageStatisticsSnapshot(lifetime, session);
    }

    private UsageStatisticsTotals LoadLifetimeTotals()
    {
        Dictionary<string, long> metrics = new(StringComparer.Ordinal);

        try
        {
            using var connection = _db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT metric, value FROM usage_statistics;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                metrics[reader.GetString(0)] = reader.GetInt64(1);
            }
        }
        catch (Exception ex)
        {
            LogFailedLoad(_logger, ex);
        }

        return new UsageStatisticsTotals(
            GetMetric(metrics, DownloadedBytesMetric),
            GetMetric(metrics, UploadedBytesMetric),
            GetMetric(metrics, AppliedDataBytesMetric),
            GetMetric(metrics, ViewedTrianglesMetric),
            GetMetric(metrics, ViewedVramBytesMetric));
    }

    private void AddMetric(string metric, long value)
    {
        if (value <= 0) return;

        AddMetrics((metric, value));
    }

    private void AddMetrics(params (string Metric, long Value)[] metrics)
    {
        var positiveMetrics = metrics.Where(metric => metric.Value > 0).ToArray();
        if (positiveMetrics.Length == 0) return;

        try
        {
            using var writeScope = _db.EnterWrite();
            using var connection = _db.Open();
            using var transaction = connection.BeginTransaction();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO usage_statistics (metric, value)
VALUES ($metric, $value)
ON CONFLICT(metric) DO UPDATE SET value = usage_statistics.value + excluded.value;";

            var metricParameter = command.CreateParameter();
            metricParameter.ParameterName = "$metric";
            metricParameter.SqliteType = SqliteType.Text;
            command.Parameters.Add(metricParameter);

            var valueParameter = command.CreateParameter();
            valueParameter.ParameterName = "$value";
            valueParameter.SqliteType = SqliteType.Integer;
            command.Parameters.Add(valueParameter);

            foreach (var metric in positiveMetrics)
            {
                metricParameter.Value = metric.Metric;
                valueParameter.Value = metric.Value;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            LogFailedPersist(_logger, ex);
        }
    }

    private static long GetMetric(Dictionary<string, long> metrics, string metric)
    {
        return metrics.TryGetValue(metric, out var value)
            ? value
            : 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load usage statistics")]
    private static partial void LogFailedLoad(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to persist usage statistics")]
    private static partial void LogFailedPersist(ILogger logger, Exception exception);
}

public readonly record struct UsageStatisticsSnapshot(UsageStatisticsTotals Lifetime, UsageStatisticsTotals Session);

public readonly record struct UsageStatisticsTotals(
    long DownloadedBytes,
    long UploadedBytes,
    long AppliedDataBytes,
    long ViewedTriangles,
    long ViewedVramBytes);
