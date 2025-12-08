using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueRegistrationResponseDto(bool Success, bool WasUpdate)
{
    public bool Success { get; set; } = Success;
    public bool WasUpdate { get; set; } = WasUpdate;
    public string? ErrorMessage { get; set; }
}