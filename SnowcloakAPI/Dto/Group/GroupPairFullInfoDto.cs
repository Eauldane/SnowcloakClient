using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Dto.Group;

[MessagePackObject(keyAsPropertyName: true)]
public record GroupPairFullInfoDto(GroupData Group, UserData User, GroupUserInfo GroupPairStatusInfo, GroupUserPermissions GroupUserPermissions) : GroupPairDto(Group, User)
{
    public GroupUserInfo GroupPairStatusInfo { get; set; } = GroupPairStatusInfo;
    public GroupUserPermissions GroupUserPermissions { get; set; } = GroupUserPermissions;
}