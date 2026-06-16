using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class PlayerPerformanceConfigService : ConfigDocument<PlayerPerformanceConfig>
{
    public const string ConfigName = "playerperformance.json";

    public PlayerPerformanceConfigService(ConfigStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
