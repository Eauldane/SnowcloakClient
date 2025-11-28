using Snowcloak.API.Dto.User;
using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.Group;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record GroupChatMsgDto(GroupDto Group, SignedChatMessage Message)
{
    public GroupDto Group = Group;
    public SignedChatMessage Message = Message;
}