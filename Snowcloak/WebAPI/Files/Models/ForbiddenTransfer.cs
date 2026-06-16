namespace Snowcloak.WebAPI.Files.Models;

public enum ForbiddenTransferKind
{
    Download,
    Upload
}

public sealed record ForbiddenTransfer(string Hash, string ForbiddenBy, ForbiddenTransferKind Kind, string? LocalFile = null)
{
    public string DisplayName => Kind == ForbiddenTransferKind.Upload && !string.IsNullOrEmpty(LocalFile)
        ? LocalFile
        : Hash;
}
