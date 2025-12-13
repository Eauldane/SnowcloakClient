using MessagePack;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record PairingAvailabilityDeltaDto(
    IReadOnlyCollection<string> AddedIdents,
    IReadOnlyCollection<string> RemovedIdents);