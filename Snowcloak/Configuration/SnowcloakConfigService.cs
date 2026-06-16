using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class SnowcloakConfigService : ConfigDocument<SnowcloakConfig>
{
    public const string ConfigName = "config.json";

    public SnowcloakConfigService(ConfigStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
