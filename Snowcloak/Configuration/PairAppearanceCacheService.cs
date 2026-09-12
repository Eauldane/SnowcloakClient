using Snowcloak.API.Data;
using Snowcloak.API.Data.Extensions;

namespace Snowcloak.Configuration;

public sealed class PairAppearanceCacheService
{
    private const int MaxEntries = 256;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PairAppearanceCacheEntry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string uid, string ident, out PairAppearanceCacheEntry entry)
    {
        entry = null!;
        var key = CacheKey(uid, ident);
        if (key == null)
        {
            return false;
        }

        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var cached)
                || cached.UpdatedUtc < DateTime.UtcNow - Retention)
            {
                _entries.Remove(key);
                return false;
            }

            entry = new PairAppearanceCacheEntry
            {
                CharacterData = cached.CharacterData.Clone(),
                DataVersion = cached.DataVersion,
                UpdatedUtc = cached.UpdatedUtc,
            };
            return true;
        }
    }

    public void Store(string uid, string ident, CharacterData data, long dataVersion)
    {
        var key = CacheKey(uid, ident);
        if (key == null)
        {
            return;
        }

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing)
                && dataVersion > 0
                && existing.DataVersion == dataVersion)
            {
                return;
            }

            var now = DateTime.UtcNow;
            _entries[key] = new PairAppearanceCacheEntry
            {
                CharacterData = data.Clone(),
                DataVersion = dataVersion,
                UpdatedUtc = now,
            };
            _entries.Remove(uid);
            Prune(now);
        }
    }

    private void Prune(DateTime now)
    {
        var expired = _entries
            .Where(pair => pair.Value.UpdatedUtc < now - Retention)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in expired)
        {
            _entries.Remove(key);
        }

        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var key in _entries
                     .OrderByDescending(pair => pair.Value.UpdatedUtc)
                     .Skip(MaxEntries)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _entries.Remove(key);
        }
    }

    private static string? CacheKey(string uid, string ident)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(ident))
        {
            return null;
        }

        return uid + "|" + ident;
    }
}

public sealed class PairAppearanceCacheEntry
{
    public CharacterData CharacterData { get; set; } = new();
    public long DataVersion { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
