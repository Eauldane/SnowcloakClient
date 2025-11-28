using Snowcloak.Configuration.Models;

namespace Snowcloak.Configuration.Configurations;

[Serializable]
public class ServerTagConfig : ISnowcloakConfiguration
{
    public Dictionary<string, ServerTagStorage> ServerTagStorage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Version { get; set; } = 0;
}