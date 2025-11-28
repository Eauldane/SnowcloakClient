using Snowcloak.API.Data;
using MessagePack;

namespace Snowcloak.API.Dto;

[MessagePackObject(keyAsPropertyName: true)]
public record AuthReplyDto
{
    public string Token { get; set; } = string.Empty;
    public string? WellKnown { get; set; }
}