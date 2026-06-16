using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class XivDataStorageService : StateDocument<XivDataStorageConfig>
{
    public const string ConfigName = "xivdatastorage.json";

    public XivDataStorageService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
