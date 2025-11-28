using MessagePack;
using Snowcloak.API.Data;

namespace Snowcloak.API.Dto.CharaData;

[MessagePackObject(keyAsPropertyName: true)]
public record CharaDataDownloadDto(string Id, UserData Uploader) : CharaDataDto(Id, Uploader)
{
    public string GlamourerData { get; init; } = string.Empty;
    public string CustomizeData { get; init; } = string.Empty;
    public string ManipulationData { get; set; } = string.Empty;
    public List<GamePathEntry> FileGamePaths { get; init; } = [];
    public List<GamePathEntry> FileSwaps { get; init; } = [];
}