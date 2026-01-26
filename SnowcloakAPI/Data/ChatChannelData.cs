using MessagePack;

namespace Snowcloak.API.Data;

[MessagePackObject(keyAsPropertyName: true)]
public record ChatChannelData(string ChannelId, string Name, string? Topic = null, bool IsPrivate = false)
{
    public string ChannelId { get; set; } = ChannelId;
    public string Name { get; set; } = Name;
    public string? Topic { get; set; } = Topic;
    public bool IsPrivate { get; set; } = IsPrivate;
}
