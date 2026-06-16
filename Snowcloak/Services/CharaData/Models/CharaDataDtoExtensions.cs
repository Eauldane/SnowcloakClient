using Snowcloak.API.Dto.CharaData;

namespace Snowcloak.Services.CharaData.Models;

public static class CharaDataDtoExtensions
{
    public static CharaDataDownloadDto ToDownloadDto(this CharaDataFullDto dto) => new(dto.Id, dto.Uploader)
    {
        CustomizeData = dto.CustomizeData,
        Description = dto.Description,
        FileGamePaths = dto.FileGamePaths,
        GlamourerData = dto.GlamourerData,
        FileSwaps = dto.FileSwaps,
        ManipulationData = dto.ManipulationData,
        UpdatedDate = dto.UpdatedDate,
    };

    public static CharaDataMetaInfoDto ToMetaInfoDto(this CharaDataFullDto dto) => new(dto.Id, dto.Uploader)
    {
        CanBeDownloaded = true,
        Description = dto.Description,
        PoseData = dto.PoseData,
        UpdatedDate = dto.UpdatedDate,
    };
}
