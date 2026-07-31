using System.Text.Json.Serialization;

namespace Snowcloak.Configuration.Configurations;

public sealed class ChatSyncStateConfig : ISnowcloakConfiguration
{
    [JsonInclude]
    public Dictionary<string, Dictionary<string, string>> HistoryHeadsByServer { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public int Version { get; set; }
}
