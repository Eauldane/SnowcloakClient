namespace Snowcloak.Core.PlayerData;

public readonly record struct CharacterIdentity(ulong ContentId, string Name, uint HomeWorldId)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => ContentId != 0 && HomeWorldId != 0 && !string.IsNullOrWhiteSpace(Name) && Name != "--";
}
