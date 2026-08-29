using Snowcloak.Core.PlayerData;

namespace Snowcloak.Configuration.Configurations;

public sealed class CharacterIdentityConfig : ISnowcloakConfiguration
{
    public Dictionary<string, Dictionary<ulong, Guid>> CompletedBindings { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; } = 1;
    public Dictionary<string, List<CharacterIdentity>> Servers { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<ulong, List<string>>> PendingLegacyIdents { get; set; } = new(StringComparer.Ordinal);
}
