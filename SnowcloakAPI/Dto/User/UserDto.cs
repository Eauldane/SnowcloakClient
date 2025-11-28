using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record UserDto(UserData User);