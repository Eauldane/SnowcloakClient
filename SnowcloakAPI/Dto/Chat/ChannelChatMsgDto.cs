using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelChatMsgDto(ChannelDto Channel, SignedChatMessage Message)
{
    public ChannelDto Channel = Channel;
    public SignedChatMessage Message = Message;
}
