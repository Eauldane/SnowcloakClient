using MessagePack;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record PairingRequestDecisionDto(Guid RequestId, bool Accepted, string? Reason = null);