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

    public bool TryGet(string uid, out PairAppearanceCacheEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(uid) || !Current.Entries.TryGetValue(uid, out var cached))
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

    public void Store(string uid, CharacterData data, long dataVersion)
    {
        if (string.IsNullOrWhiteSpace(uid))
        {
            return;
        }

        Update(config =>
        {
            config.Entries[uid] = new PairAppearanceCacheEntry
            {
                CharacterData = data.Clone(),
                DataVersion = dataVersion,
                UpdatedUtc = DateTime.UtcNow,
            };
        });
    }
}
