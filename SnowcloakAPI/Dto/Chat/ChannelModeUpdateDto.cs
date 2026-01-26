using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelModeUpdateDto(ChatChannelData Channel, ChannelModeFlags Modes, int SlowModeSeconds = 0)
{
    public ChatChannelData Channel { get; set; } = Channel;
    public ChannelModeFlags Modes { get; set; } = Modes;
    public int SlowModeSeconds { get; set; } = SlowModeSeconds;
}
