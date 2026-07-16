using Snowcloak.API.Dto.Chat;
using Snowcloak.Core.Chat;

namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048
public record UserChatMsgMessage(UserChatMsgDto Dto) : MessageBase;
public record GroupChatMsgMessage(GroupChatMsgDto Dto) : MessageBase;
public record RoomChatMsgMessage(RoomChatMsgDto Dto) : MessageBase;
public record RoomMemberJoinedMessage(RoomMemberJoinedDto Dto) : MessageBase;
public record RoomMemberLeftMessage(RoomMemberLeftDto Dto) : MessageBase;
public record ChatMembershipChangedMessage : MessageBase;
public record ChatIncomingAppendedMessage(ConversationKey Key, ChatEntry Entry) : MessageBase;
public record OpenChatPopoutMessage(ConversationKey Key) : MessageBase;
public record OpenRoomAdministrationMessage(string RoomId) : MessageBase;
public record OpenChatSettingsMessage : SameThreadMessage;
public record ChatOutgoingStampedMessage(ConversationKey Key, ChatEntry Entry) : MessageBase;
#pragma warning restore MA0048
