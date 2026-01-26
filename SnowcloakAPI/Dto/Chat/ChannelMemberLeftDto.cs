using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelMemberLeftDto(ChatChannelData Channel, UserData User)
{
    public ChatChannelData Channel { get; set; } = Channel;
    public UserData User { get; set; } = User;
}
