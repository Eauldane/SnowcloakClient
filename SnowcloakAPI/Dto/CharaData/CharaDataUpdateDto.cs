using MessagePack;

namespace Snowcloak.API.Dto.CharaData;

[MessagePackObject(keyAsPropertyName: true)]
public record CharaDataUpdateDto(string Id)
{
    public string? Description { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? GlamourerData { get; set; }
    public string? CustomizeData { get; set; }
    public string? ManipulationData { get; set; }
    public List<string>? AllowedUsers { get; set; }
    public List<string>? AllowedGroups { get; set; }
    public List<GamePathEntry>? FileGamePaths { get; set; }
    public List<GamePathEntry>? FileSwaps { get; set; }
    public AccessTypeDto? AccessType { get; set; }
    public ShareTypeDto? ShareType { get; set; }
    public List<PoseEntry>? Poses { get; set; }
}
