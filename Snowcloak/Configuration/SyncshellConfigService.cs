using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class SyncshellConfigService : StateDocument<SyncshellConfig>
{
    public const string ConfigName = "syncshells.json";

    public SyncshellConfigService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
