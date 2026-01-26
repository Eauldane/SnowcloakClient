using MessagePack;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelCreateDto(string Name, string? Topic = null, bool IsPrivate = false)
{
    public string Name { get; set; } = Name;
    public string? Topic { get; set; } = Topic;
    public bool IsPrivate { get; set; } = IsPrivate;
}
