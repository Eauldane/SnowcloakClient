using MessagePack;

namespace MareSynchronos.API.Data;

[MessagePackObject(keyAsPropertyName: true)]
public record UserData(string UID, string? Alias = null, string? HexString = null)
{
    [IgnoreMember]
    public string AliasOrUID => string.IsNullOrWhiteSpace(Alias) ? UID : Alias;
    [IgnoreMember]
    public string? DisplayColour => string.IsNullOrWhiteSpace(HexString) ? null : HexString;
}