using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class ServerTagConfigService : StateDocument<ServerTagConfig>
{
    public const string ConfigName = "servertags.json";

    public ServerTagConfigService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
