using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class TransientConfigService : StateDocument<TransientConfig>
{
    public const string ConfigName = "transient.json";

    public TransientConfigService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
