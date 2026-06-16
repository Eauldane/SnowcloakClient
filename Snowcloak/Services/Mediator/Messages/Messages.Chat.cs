using Snowcloak.API.Data;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;

namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
public record UserChatMsgMessage(SignedChatMessage ChatMsg) : MessageBase;
public record GroupChatMsgMessage(GroupDto GroupInfo, SignedChatMessage ChatMsg) : MessageBase;
public record GroupChatMemberStateMessage(GroupChatMemberStateDto MemberState) : MessageBase;
public record ChannelChatMsgMessage(ChannelDto ChannelInfo, SignedChatMessage ChatMsg) : MessageBase;
public record ChannelMemberJoinedMessage(ChannelMemberJoinedDto Member) : MessageBase;
public record ChannelMemberLeftMessage(ChannelMemberLeftDto Member) : MessageBase;
public record StandardChannelMembershipChangedMessage(ChatChannelData Channel, bool IsJoined) : MessageBase;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
