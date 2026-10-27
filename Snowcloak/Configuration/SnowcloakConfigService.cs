using Snowcloak.Configuration.Configurations;
using Snowcloak.Configuration.Models;

namespace Snowcloak.Configuration;

public class SnowcloakConfigService : ConfigDocument<SnowcloakConfig>
{
    public const string ConfigName = "config.json";

    public SnowcloakConfigService(ConfigStore store) : base(store)
    {
        if (Current.ChatSoundDefaultsVersion < 1)
        {
            Update(config =>
            {
                config.DefaultChatSound = ChatSoundOption.Sound14;
                config.ChatSoundDefaultsVersion = 1;
            });
        }
    }

    public override string FileName => ConfigName;
}
