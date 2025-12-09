using MessagePack;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record PublicUserProfileDto(string Ident, bool Disabled, bool? IsNSFW, string? ProfilePictureBase64, string? Description);