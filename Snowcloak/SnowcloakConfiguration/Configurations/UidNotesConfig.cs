using Snowcloak.Configuration.Models;

namespace Snowcloak.Configuration.Configurations;

[Serializable]
public class UidNotesConfig : ISnowcloakConfiguration
{
    public Dictionary<string, ServerNotesStorage> ServerNotes { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; } = 0;
}
