using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Dto.Group;

[MessagePackObject(keyAsPropertyName: true)]
public record GroupFullInfoDto(GroupData Group, UserData Owner, GroupPermissions GroupPermissions, GroupUserPermissions GroupUserPermissions, GroupUserInfo GroupUserInfo) : GroupInfoDto(Group, Owner, GroupPermissions)
{
    public GroupUserPermissions GroupUserPermissions { get; set; } = GroupUserPermissions;
    public GroupUserInfo GroupUserInfo { get; set; } = GroupUserInfo;
}