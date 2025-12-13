using MessagePack;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record PairingAvailabilitySubscriptionDto(
    uint WorldId,
    uint TerritoryId,
    IReadOnlyCollection<string> NearbyIdents,
    IReadOnlyCollection<string> AddedNearbyIdents,
    IReadOnlyCollection<string> RemovedNearbyIdents);