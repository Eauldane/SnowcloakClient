namespace Snowcloak.WebAPI.Files.Models;

public sealed record UploadStatusSnapshot(string Hash, long Transferred, long Total)
{
    public bool IsTransferred => Total > 0 && Transferred >= Total;
}
