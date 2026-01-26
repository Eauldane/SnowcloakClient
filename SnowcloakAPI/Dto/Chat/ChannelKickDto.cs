using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.User;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelKickDto(ChatChannelData Channel, UserData User, string? Reason = null) : UserDto(User)
{
    public ChatChannelData Channel { get; set; } = Channel;
    public UserData User { get; set; } = User;
    public string? Reason { get; set; } = Reason;
}
