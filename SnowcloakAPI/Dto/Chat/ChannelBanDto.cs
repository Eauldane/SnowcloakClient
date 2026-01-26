using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.User;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelBanDto(ChatChannelData Channel, UserData User, int DurationMinutes = 0, string? Reason = null) : UserDto(User)
{
    public ChatChannelData Channel { get; set; } = Channel;
    public UserData User { get; set; } = User;
    public int DurationMinutes { get; set; } = DurationMinutes;
    public string? Reason { get; set; } = Reason;
}
