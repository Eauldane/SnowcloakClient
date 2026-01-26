using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelDto(ChatChannelData Channel)
{
    public ChatChannelData Channel { get; set; } = Channel;
    public string ChannelId => Channel.ChannelId;
    public string Name => Channel.Name;
}
