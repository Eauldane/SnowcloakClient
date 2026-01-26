using MessagePack;

namespace Snowcloak.API.Dto.Venue;

[MessagePackObject(keyAsPropertyName: true)]
public record VenueRegistryGetRequestDto(Guid? RegistryId, string? SyncshellGid)
{
    public Guid? RegistryId { get; set; } = RegistryId;
    public string? SyncshellGid { get; set; } = SyncshellGid;
}
