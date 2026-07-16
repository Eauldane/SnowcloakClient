using Snowcloak.Configuration.Models;

namespace Snowcloak.Configuration.Configurations;

public sealed class ChatPreferencesConfig : ISnowcloakConfiguration
{
    public Dictionary<string, ConversationPrefs> Conversations { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; }
}
