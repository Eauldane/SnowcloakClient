using Snowcloak.Configuration.Models;

namespace Snowcloak.Configuration.Configurations;

[Serializable]
public class SyncshellConfig : ISnowcloakConfiguration
{
    public Dictionary<string, ServerShellStorage> ServerShellStorage { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; } = 0;
}