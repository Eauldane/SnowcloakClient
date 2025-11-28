using Snowcloak.Configuration.Models;

namespace Snowcloak.Configuration.Configurations;

[Serializable]
public class ServerBlockConfig : ISnowcloakConfiguration
{
    public Dictionary<string, ServerBlockStorage> ServerBlocks { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; } = 0;
}