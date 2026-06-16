using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class CharaDataConfigService : ConfigDocument<CharaDataConfig>
{
    public const string ConfigName = "charadata.json";

    public CharaDataConfigService(ConfigStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
