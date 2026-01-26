using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelTopicUpdateDto(ChatChannelData Channel, string? Topic)
{
    public ChatChannelData Channel { get; set; } = Channel;
    public string? Topic { get; set; } = Topic;
}