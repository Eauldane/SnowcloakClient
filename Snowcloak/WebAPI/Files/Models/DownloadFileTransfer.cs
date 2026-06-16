using Snowcloak.API.Dto.Files;

namespace Snowcloak.WebAPI.Files.Models;

public class DownloadFileTransfer : FileTransfer
{
    public DownloadFileTransfer(DownloadFileDto dto) : base(dto)
    {
    }

    public override bool CanBeTransferred => Dto.FileExists && !Dto.IsForbidden && Dto.Size > 0;
    public Uri DownloadUri => new(Dto.Url);
    public override long Total
    {
        set
        {
            _ = value;
        }
        get => Dto.Size;
    }

    public long TotalRaw => Dto.Size;
    private DownloadFileDto Dto => (DownloadFileDto)TransferDto;
}
