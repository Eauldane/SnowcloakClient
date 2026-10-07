using System.Net;
using System.Net.Http.Headers;

namespace Snowcloak.WebAPI.Files.Models;

internal static class UploadRateLimitRetry
{
    private const int MaxAttempts = 6;

    public static async Task<HttpResponseMessage> SendAsync(Stream source,
        Func<Stream, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= (duration, token) => Task.Delay(duration, token);
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (source.CanSeek) source.Position = 0;
            using var body = new BorrowedStream(source);
            var response = await send(body, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxAttempts || !source.CanSeek)
                return response;
            var wait = RetryDelay(response.Headers.RetryAfter, attempt);
            response.Dispose();
            await delay(wait, ct).ConfigureAwait(false);
        }
    }

    internal static TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        var value = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        if (value is not { } duration || duration <= TimeSpan.Zero)
            duration = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, attempt - 1)));
        return duration < TimeSpan.FromMinutes(1) ? duration : TimeSpan.FromMinutes(1);
    }

    private sealed class BorrowedStream(Stream source) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => source.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => source.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
