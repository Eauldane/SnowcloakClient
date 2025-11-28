using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class ServerBlockConfigService : ConfigurationServiceBase<ServerBlockConfig>
{
    public const string ConfigName = "blocks.json";

    public ServerBlockConfigService(string configDir) : base(configDir)
    {
    }

    public override string ConfigurationName => ConfigName;
}