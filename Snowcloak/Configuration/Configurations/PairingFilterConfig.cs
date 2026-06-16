using Snowcloak.Configuration.Models;

namespace Snowcloak.Configuration.Configurations;

public class PairingFilterConfig : ISnowcloakConfiguration
{
    public HashSet<AutoRejectCombo> AutoRejectCombos { get; set; } = [];
    public HashSet<ushort> PairRequestRejectedHomeworlds { get; set; } = [];
    public int Version { get; set; }
}
