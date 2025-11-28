using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class SyncshellConfigService : ConfigurationServiceBase<SyncshellConfig>
{
    public const string ConfigName = "syncshells.json";

    public SyncshellConfigService(string configDir) : base(configDir)
    {
    }

    public override string ConfigurationName => ConfigName;
}