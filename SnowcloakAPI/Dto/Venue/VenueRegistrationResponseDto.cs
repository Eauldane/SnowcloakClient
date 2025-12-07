using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueRegistrationResponseDto(bool Success)
{
    public bool Success { get; set; } = Success;
    public string? ErrorMessage { get; set; }
}