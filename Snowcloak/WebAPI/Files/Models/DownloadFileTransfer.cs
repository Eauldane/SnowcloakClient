using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Files;

namespace Snowcloak.WebAPI.Files.Models;

public class DownloadFileTransfer : FileTransfer
{
    public DownloadFileTransfer(DownloadFileDto dto) : base(dto)
    {
    }

    public override bool CanBeTransferred => Dto.FileExists && !Dto.IsForbidden && Dto.Size > 0;
    public Uri DownloadUri => new(Dto.Url);
    public string RepresentationId => Dto.RepresentationId;
    public FileContainerVersion ContainerVersion => Dto.ContainerVersion;
    public FileRepresentationCodec Codec => Dto.Codec;
    public string Profile => Dto.Profile;
    public long EncodedSize => Dto.EncodedSize > 0 ? Dto.EncodedSize : Dto.Size;
    public bool SupportsResume => Dto.SupportsResume
                                  && Dto.ContainerVersion == FileContainerVersion.ScfV4
                                  && Dto.EncodedSize > 0
                                  && Dto.RepresentationId.Length == 64
                                  && Dto.RepresentationId.All(Uri.IsHexDigit);
    public string TransferIdentity => Hash + ":" + (string.IsNullOrWhiteSpace(RepresentationId)
        ? DownloadUri.AbsoluteUri
        : RepresentationId);
    public void Refresh(DownloadFileDto dto)
    {
        TransferDto = dto;
    }
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
