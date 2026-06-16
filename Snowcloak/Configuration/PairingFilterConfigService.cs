using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class PairingFilterConfigService : StateDocument<PairingFilterConfig>
{
    public const string ConfigName = "pairing-filters.json";

    public PairingFilterConfigService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;

    public override IReadOnlyList<string> LegacyFileNames => [SnowcloakConfigService.ConfigName];
}
