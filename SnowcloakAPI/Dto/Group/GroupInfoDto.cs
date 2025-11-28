using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Dto.Group;

[MessagePackObject(keyAsPropertyName: true)]
public record GroupInfoDto(GroupData Group, UserData Owner, GroupPermissions GroupPermissions) : GroupDto(Group)
{
    public GroupPermissions GroupPermissions { get; set; } = GroupPermissions;
    public UserData Owner { get; set; } = Owner;

    public string OwnerUID => Owner.UID;
    public string? OwnerAlias => Owner.Alias;
    public string OwnerAliasOrUID => Owner.AliasOrUID;
}
