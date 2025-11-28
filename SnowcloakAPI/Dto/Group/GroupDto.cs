using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.Group;

[MessagePackObject(keyAsPropertyName: true)]
public record GroupDto(GroupData Group)
{
    public GroupData Group { get; set; } = Group;
    public string GID => Group.GID;
    public string? GroupAlias => Group.Alias;
    public string GroupAliasOrGID => Group.AliasOrGID;
}