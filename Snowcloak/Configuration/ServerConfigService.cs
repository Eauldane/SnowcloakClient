using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class ServerConfigService : ConfigDocument<ServerConfig>
{
    public const string ConfigName = "server.json";

    public ServerConfigService(ConfigStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
