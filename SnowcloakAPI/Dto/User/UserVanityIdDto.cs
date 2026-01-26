using MessagePack;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record UserVanityIdDto(string? VanityId)
{
    public string? VanityId { get; set; } = VanityId;
    public string? HexString { get; set; }
}