using Snowcloak.PlayerData.Handlers;

namespace Snowcloak.WebAPI.Files.Models;

public sealed record DownloadGroupSnapshot(
    string Server,
    DownloadStatus Status,
    long TransferredBytes,
    long TotalBytes,
    int TransferredFiles,
    int TotalFiles,
    string? StatusMessage);

public sealed record DownloadSnapshot(
    GameObjectHandler Handler,
    string? Uid,
    IReadOnlyList<DownloadGroupSnapshot> Groups)
{
    public long TransferredBytes => Groups.Sum(g => g.TransferredBytes);
    public long TotalBytes => Groups.Sum(g => g.TotalBytes);
    public int TransferredFiles => Groups.Sum(g => g.TransferredFiles);
    public int TotalFiles => Groups.Sum(g => g.TotalFiles);

    public int CountByStatus(DownloadStatus status) => Groups.Count(g => g.Status == status);
    public IReadOnlyList<string> GetUnavailableMessages() => Groups.Where(g => g.Status == DownloadStatus.Unavailable)
        .Select(g => g.StatusMessage).Where(message => !string.IsNullOrWhiteSpace(message)).Cast<string>()
        .Distinct(StringComparer.Ordinal).ToList();
}
