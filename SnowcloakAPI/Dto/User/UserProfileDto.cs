using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record UserProfileDto(UserData User, bool Disabled, bool? IsNSFW, string? ProfilePictureBase64, string? Description, ProfileVisibility? Visibility = null) : UserDto(User);