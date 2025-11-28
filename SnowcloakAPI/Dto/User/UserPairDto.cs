using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record UserPairDto(UserData User, UserPermissions OwnPermissions, UserPermissions OtherPermissions) : UserDto(User)
{
    public UserPermissions OwnPermissions { get; set; } = OwnPermissions;
    public UserPermissions OtherPermissions { get; set; } = OtherPermissions;
}