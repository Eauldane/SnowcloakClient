using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelMemberDto(ChatChannelData Channel, UserData User, ChannelUserRole Roles)
{
    public ChatChannelData Channel { get; set; } = Channel;
    public UserData User { get; set; } = User;
    public ChannelUserRole Roles { get; set; } = Roles;
}
