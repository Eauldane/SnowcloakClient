using Snowcloak.API.Data;

namespace Snowcloak.Configuration.Configurations;

public sealed class PairAppearanceCacheConfig : ISnowcloakConfiguration
{
    public int Version { get; set; }
    public Dictionary<string, PairAppearanceCacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PairAppearanceCacheEntry
{
    public CharacterData CharacterData { get; set; } = new();
    public long DataVersion { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
