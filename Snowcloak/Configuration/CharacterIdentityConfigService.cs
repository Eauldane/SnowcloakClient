using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public sealed class CharacterIdentityConfigService(ConfigStore store) : ConfigDocument<CharacterIdentityConfig>(store)
{
    public override string FileName => "character-identities.json";
}
