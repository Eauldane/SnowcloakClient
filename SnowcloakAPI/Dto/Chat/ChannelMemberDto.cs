using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;

namespace Snowcloak.API.Dto.Chat;

[MessagePackObject(keyAsPropertyName: true)]
public record ChannelMemberDto(ChatChannelData Channel, UserData User, ChannelUserRole Roles) : UserDto(User)
{
    public ChatChannelData Channel { get; set; } = Channel;
    public new UserData User { get; set; } = User;
    public ChannelUserRole Roles { get; set; } = Roles;
}
