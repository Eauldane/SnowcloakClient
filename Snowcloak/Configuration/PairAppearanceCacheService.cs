using Snowcloak.API.Data;
using Snowcloak.API.Data.Extensions;
using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public sealed class PairAppearanceCacheService : StateDocument<PairAppearanceCacheConfig>
{
    public const string ConfigName = "pairappearancecache.json";

    public PairAppearanceCacheService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;

    public bool TryGet(string uid, string ident, out PairAppearanceCacheEntry entry)
    {
        entry = null!;
        var key = CacheKey(uid, ident);
        if (key == null || !Current.Entries.TryGetValue(key, out var cached))
        {
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

    public void Store(string uid, string ident, CharacterData data, long dataVersion)
    {
        var key = CacheKey(uid, ident);
        if (key == null)
        {
            return;
        }

        Update(config =>
        {
            config.Entries[key] = new PairAppearanceCacheEntry
            {
                CharacterData = data.Clone(),
                DataVersion = dataVersion,
                UpdatedUtc = DateTime.UtcNow,
            };
            config.Entries.Remove(uid);
        });
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
