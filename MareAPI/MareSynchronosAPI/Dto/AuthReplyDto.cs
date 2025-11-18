using MareSynchronos.API.Data;
using MessagePack;

namespace MareSynchronos.API.Dto;

[MessagePackObject(keyAsPropertyName: true)]
public record AuthReplyDto
{
    public string Token { get; set; } = string.Empty;
    public string? WellKnown { get; set; }
    public Homeserver? Homeserver { get; set; }
}