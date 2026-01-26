using MessagePack;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Dto;

[MessagePackObject(keyAsPropertyName: true)]
public record ConnectionModeDto(ConnectionMode Mode)
{
    public ConnectionMode Mode { get; set; } = Mode;
}
