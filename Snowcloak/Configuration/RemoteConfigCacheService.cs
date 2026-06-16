using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class RemoteConfigCacheService : StateDocument<RemoteConfigCache>
{
    public const string ConfigName = "remotecache.json";

    public RemoteConfigCacheService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
