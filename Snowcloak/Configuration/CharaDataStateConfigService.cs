using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class CharaDataStateConfigService : StateDocument<CharaDataStateConfig>
{
    public const string ConfigName = "charadata-state.json";

    public CharaDataStateConfigService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;

    public override IReadOnlyList<string> LegacyFileNames => [CharaDataConfigService.ConfigName];
}
