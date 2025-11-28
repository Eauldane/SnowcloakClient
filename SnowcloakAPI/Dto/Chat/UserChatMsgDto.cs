using Snowcloak.API.Dto.User;
using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record UserChatMsgDto(SignedChatMessage Message)
{
    public SignedChatMessage Message = Message;
}