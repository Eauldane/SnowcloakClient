using System.Collections.Concurrent;

namespace Snowcloak.Infrastructure.Transfers;

public enum FileDownloadNegativeReason
{
    Missing,
    Rejected,
    RateLimited,
    TemporarilyUnavailable,
    PrefetchBudgetExceeded,
    Forbidden,
}

public sealed record FileDownloadNegativeEntry(string Hash, FileDownloadNegativeReason Reason,
    DateTimeOffset ExpiresAt, string Message, int? QueuePosition);

public sealed class FileDownloadNegativeCache
{
    private readonly ConcurrentDictionary<string, FileDownloadNegativeEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string hash, out FileDownloadNegativeEntry entry)
    {
        var normalised = hash.ToUpperInvariant();
        if (_entries.TryGetValue(normalised, out entry!))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return true;
            }

            ((ICollection<KeyValuePair<string, FileDownloadNegativeEntry>>)_entries).Remove(new(normalised, entry));
        }

        entry = null!;
        return false;
    }

    public FileDownloadNegativeEntry Record(string hash, FileDownloadNegativeReason reason, TimeSpan lifetime,
        string message, int? queuePosition = null)
    {
        var normalised = hash.ToUpperInvariant();
        var boundedLifetime = lifetime <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : lifetime;
        var entry = new FileDownloadNegativeEntry(normalised, reason, DateTimeOffset.UtcNow.Add(boundedLifetime),
            message, queuePosition);
        return _entries.AddOrUpdate(normalised, entry, (_, current) => current.ExpiresAt >= entry.ExpiresAt ? current : entry);
    }

    public void Clear(string hash) => _entries.TryRemove(hash, out _);

    public void ClearMissing(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        var normalised = hash.ToUpperInvariant();
        if (_entries.TryGetValue(normalised, out var entry)
            && entry.Reason == FileDownloadNegativeReason.Missing)
        {
            ((ICollection<KeyValuePair<string, FileDownloadNegativeEntry>>)_entries).Remove(new(normalised, entry));
        }
    }
}
