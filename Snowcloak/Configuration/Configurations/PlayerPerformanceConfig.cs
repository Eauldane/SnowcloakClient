using Snowcloak.Configuration.Models;

namespace Snowcloak.Configuration.Configurations;

public class PlayerPerformanceConfig : ISnowcloakConfiguration
{
    public int Version { get; set; } = 1;
    public bool AutoPausePlayersExceedingThresholds { get; set; } = true;
    public bool NotifyAutoPauseDirectPairs { get; set; } = true;
    public bool NotifyAutoPauseGroupPairs { get; set; } = true;
    public int VRAMSizeAutoPauseThresholdMiB { get; set; } = 500;
    public int TrisAutoPauseThresholdThousands { get; set; } = 400;
    public bool IgnoreDirectPairs { get; set; } = true;
    public TextureShrinkMode TextureShrinkMode { get; set; } = TextureShrinkMode.Default;
    public bool TextureShrinkDeleteOriginal { get; set; } = false;
}