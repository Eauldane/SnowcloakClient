using Snowcloak.WebAPI.Files.Models;

namespace Snowcloak.WebAPI.Files;

public sealed record DownloadGroupRequest(Uri DownloadUri, IReadOnlyList<string> Hashes, string? DownloadType);

public sealed class DownloadResponse : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;

    public DownloadResponse(HttpResponseMessage response, Stream stream, long? reportedTotalBytes)
    {
        _response = response;
        Stream = stream;
        ReportedTotalBytes = reportedTotalBytes;
    }

    public Stream Stream { get; }
    public long? ReportedTotalBytes { get; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
    }
}

public interface IFileDownloadTransport
{
    Task<DownloadResponse> OpenAsync(DownloadGroupRequest request, Action<DownloadStatus>? onPhase, CancellationToken ct);
}
