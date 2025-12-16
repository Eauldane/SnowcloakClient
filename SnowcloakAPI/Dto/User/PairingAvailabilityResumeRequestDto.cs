using MessagePack;
using System;

namespace Snowcloak.API.Dto.User;

[MessagePackObject(keyAsPropertyName: true)]
public record PairingAvailabilityResumeRequestDto(
    uint WorldId,
    uint TerritoryId,
    int NearbyIdentsCount,
    string ResumeToken,
    DateTimeOffset SavedAt);