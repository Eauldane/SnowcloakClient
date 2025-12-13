using MessagePack;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record UserProfileRequestDto(UserData? User, string? Ident, ProfileVisibility? Visibility);