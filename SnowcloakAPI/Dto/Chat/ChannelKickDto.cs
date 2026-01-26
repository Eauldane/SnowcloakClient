using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelKickDto(ChatChannelData Channel, UserData User, string? Reason = null)
{
    public ChatChannelData Channel { get; set; } = Channel;
    public UserData User { get; set; } = User;
    public string? Reason { get; set; } = Reason;
}
