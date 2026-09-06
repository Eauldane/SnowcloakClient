namespace Snowcloak.WebAPI.Files.Models;

public enum DownloadStatus
{
    Initializing,
    WaitingForSlot,
    WaitingForQueue,
    ValidatingPartial,
    Downloading,
    Resuming,
    WaitingForDecompression,
    Decompressing,
    Unavailable,
}
