using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.Group;

[MessagePackObject(keyAsPropertyName: true)]
public record GroupPasswordDto(GroupData Group, string Password) : GroupDto(Group);
