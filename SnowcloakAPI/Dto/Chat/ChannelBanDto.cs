using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelBanDto(ChatChannelData Channel, UserData User, int DurationMinutes, string? Reason = null)
{
    public ChatChannelData Channel { get; set; } = Channel;
    public UserData User { get; set; } = User;
    public int DurationMinutes { get; set; } = DurationMinutes;
    public string? Reason { get; set; } = Reason;
}
