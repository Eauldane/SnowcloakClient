using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class VenueStateConfigService : StateDocument<VenueStateConfig>
{
    public const string ConfigName = "venue-state.json";

    public VenueStateConfigService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;

    public override IReadOnlyList<string> LegacyFileNames => [SnowcloakConfigService.ConfigName];
}
