using Snowcloak.Configuration;
using Snowcloak.Configuration.Configurations;

namespace Snowcloak.SnowcloakConfiguration;

public class SnowcloakConfigService : ConfigurationServiceBase<SnowcloakConfig>
{
    public const string ConfigName = "config.json";

    public SnowcloakConfigService(string configDir) : base(configDir)
    {
    }

    public override string ConfigurationName => ConfigName;
}