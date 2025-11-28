using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record UserProfileReportDto(UserData User, string ProfileReport) : UserDto(User);