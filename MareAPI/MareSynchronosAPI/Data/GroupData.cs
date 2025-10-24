using MessagePack;

namespace MareSynchronos.API.Data;

[MessagePackObject(keyAsPropertyName: true)]
public record GroupData(string GID, string? Alias = null, string? HexString = null)
{
    [IgnoreMember]
    public string AliasOrGID => string.IsNullOrWhiteSpace(Alias) ? GID : Alias;
    [IgnoreMember]
    public string? DisplayColour => string.IsNullOrWhiteSpace(HexString) ? null : HexString;
}