using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Files;
using Snowcloak.API.Routes;

namespace Snowcloak.WebAPI.Files;

public sealed class ImageTransferService : IDisposable
{
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly ILogger<ImageTransferService> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();

    public ImageTransferService(ILogger<ImageTransferService> logger, FileTransferOrchestrator orchestrator)
    {
        _logger = logger;
        _orchestrator = orchestrator;
    }

    public async Task<ImageUploadReplyDto?> UploadImageAsync(byte[] imageData, ImageKind kind, CancellationToken token)
    {
        if (!_orchestrator.IsInitialized) throw new InvalidOperationException("File transfer is not initialised.");

        using var content = new ByteArrayContent(imageData);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var response = await _orchestrator.SendRequestAsync(HttpMethod.Post,
            SnowFiles.ImageUploadFullPath(_orchestrator.FilesCdnUri!, kind), content, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            _logger.LogWarning("Image upload failed with {status}: {body}", response.StatusCode, body);
            throw new ImageUploadException(DescribeUploadFailure(response.StatusCode, body));
        }

        var reply = await response.Content.ReadFromJsonAsync<ImageUploadReplyDto>(cancellationToken: token).ConfigureAwait(false);
        if (reply != null && !string.IsNullOrEmpty(reply.Hash))
        {
            _cache[reply.Hash.ToUpperInvariant()] = imageData;
        }

        return reply;
    }

    private static string DescribeUploadFailure(HttpStatusCode status, string? body)
    {
        return status switch
        {
            HttpStatusCode.RequestEntityTooLarge => "That image is too large to upload. Use a smaller PNG.",
            HttpStatusCode.BadRequest when !string.IsNullOrWhiteSpace(body) && !LooksLikeHtml(body) => body!.Trim(),
            HttpStatusCode.BadRequest => "That image could not be accepted. Make sure it is a valid PNG.",
            HttpStatusCode.NotFound => "The image service is unavailable right now. Please try again later.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "You are not authorised to upload images. Reconnect and try again.",
            _ => $"The image upload failed ({(int)status}). Please try again later.",
        };
    }

    private static bool LooksLikeHtml(string body)
        => body.TrimStart().StartsWith('<');

    public bool TryGetImage(string? hash, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        var normalized = hash.ToUpperInvariant();
        if (_cache.TryGetValue(normalized, out var cached))
        {
            bytes = cached;
            return cached.Length > 0;
        }

        TriggerFetch(normalized);
        return false;
    }

    private void TriggerFetch(string hash)
    {
        if (!_orchestrator.IsInitialized || !_inFlight.TryAdd(hash, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _cache[hash] = await DownloadAsync(hash, _disposeCts.Token).ConfigureAwait(false) ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not download image {hash}", hash);
                _cache[hash] = [];
            }
            finally
            {
                _inFlight.TryRemove(hash, out _);
            }
        });
    }

    private async Task<byte[]?> DownloadAsync(string hash, CancellationToken token)
    {
        if (!_orchestrator.IsInitialized) return null;

        using var response = await _orchestrator.SendRequestAsync(HttpMethod.Get,
            SnowFiles.ImageGetFullPath(_orchestrator.FilesCdnUri!, hash), token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }
}

public sealed class ImageUploadException : Exception
{
    public ImageUploadException(string message) : base(message)
    {
    }
}
