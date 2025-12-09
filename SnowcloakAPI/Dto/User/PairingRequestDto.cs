using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record PairingRequestDto(Guid RequestId, UserData Requester, string RequesterIdent, DateTimeOffset RequestedAt) : UserDto(Requester);