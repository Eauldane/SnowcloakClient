using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class CharaDataConfigService : ConfigurationServiceBase<CharaDataConfig>
{
    public const string ConfigName = "charadata.json";

    public CharaDataConfigService(string configDir) : base(configDir) { }
    public override string ConfigurationName => ConfigName;
}