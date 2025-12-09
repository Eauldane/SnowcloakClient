using MessagePack;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record PairingAvailabilityQueryDto(IReadOnlyCollection<string> NearbyIdents);