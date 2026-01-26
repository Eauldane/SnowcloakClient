using MessagePack;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelCreateDto(string Name, string? Topic = null, bool IsPrivate = false, ChannelType Type = ChannelType.Standard)
{
    public string Name { get; set; } = Name;
    public string? Topic { get; set; } = Topic;
    public bool IsPrivate { get; set; } = IsPrivate;
    public ChannelType Type { get; set; } = Type;
}
