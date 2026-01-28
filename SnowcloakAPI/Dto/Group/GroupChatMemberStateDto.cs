using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.Group;

[MessagePackObject(keyAsPropertyName: true)]
public record GroupChatMemberStateDto(GroupDto Group, UserData User, bool IsJoined)
{
    public GroupDto Group { get; set; } = Group;
    public UserData User { get; set; } = User;
    public bool IsJoined { get; set; } = IsJoined;
}
