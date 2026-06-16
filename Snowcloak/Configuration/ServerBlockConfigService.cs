using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class ServerBlockConfigService : StateDocument<ServerBlockConfig>
{
    public const string ConfigName = "blocks.json";

    public ServerBlockConfigService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
