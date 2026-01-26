using MessagePack;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelCreateDto(string Name, Data.Enum.ChannelType Type = Data.Enum.ChannelType.Standard, string? Topic = null, bool IsPrivate = false)
{
    public string Name { get; set; } = Name;
    public Data.Enum.ChannelType Type { get; set; } = Type;
    public string? Topic { get; set; } = Topic;
    public bool IsPrivate { get; set; } = IsPrivate;
}
