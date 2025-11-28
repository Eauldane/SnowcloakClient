using Snowcloak.API.Dto.CharaData;
using System.Collections.ObjectModel;

namespace Snowcloak.Services.CharaData.Models;

public sealed record CharaDataFullExtendedDto : CharaDataFullDto
{
    public CharaDataFullExtendedDto(CharaDataFullDto baseDto) : base(baseDto)
    {
        FullId = baseDto.Uploader.UID + ":" + baseDto.Id;
        MissingFiles = new ReadOnlyCollection<GamePathEntry>(baseDto.OriginalFiles.Except(baseDto.FileGamePaths).ToList());
        HasMissingFiles = MissingFiles.Any();
    }

    public string FullId { get; set; }
    public bool HasMissingFiles { get; init; }
    public IReadOnlyCollection<GamePathEntry> MissingFiles { get; init; }
}
