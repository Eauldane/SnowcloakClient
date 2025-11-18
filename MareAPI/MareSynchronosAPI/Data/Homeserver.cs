using MessagePack;

namespace MareSynchronos.API.Data;

[MessagePackObject(keyAsPropertyName: true)]
public record Homeserver(string Url, string DisplayName, string? TrustUrl = null)
{

}