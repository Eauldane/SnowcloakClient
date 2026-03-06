using System.Buffers;
using System.Diagnostics;
using System.Net;

namespace Snowcloak.WebAPI.Files.Models;

public class ProgressableStreamContent : StreamContent
{
    private const int _defaultBufferSize = 64 * 1024;
    private const long _progressReportByteInterval = 256 * 1024;
    private static readonly TimeSpan _progressReportMinInterval = TimeSpan.FromMilliseconds(100);
    private readonly int _bufferSize;
    private readonly IProgress<UploadProgress>? _progress;
    private readonly Stream _streamToWrite;
    private bool _contentConsumed;

    public ProgressableStreamContent(Stream streamToWrite, IProgress<UploadProgress>? downloader)
        : this(streamToWrite, _defaultBufferSize, downloader)
    {
    }

    public ProgressableStreamContent(Stream streamToWrite, int bufferSize, IProgress<UploadProgress>? progress)
        : base(streamToWrite, bufferSize)
    {
        if (streamToWrite == null)
        {
            throw new ArgumentNullException(nameof(streamToWrite));
        }

        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        }

        _streamToWrite = streamToWrite;
        _bufferSize = bufferSize;
        _progress = progress;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _streamToWrite.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        PrepareContent();

        var size = _streamToWrite.Length;
        long uploaded = 0;
        long pendingProgressBytes = 0;
        long lastProgressReportTimestamp = Stopwatch.GetTimestamp();
        var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);

        try
        {
            while (true)
            {
                var length = await _streamToWrite.ReadAsync(buffer.AsMemory(0, _bufferSize)).ConfigureAwait(false);
                if (length <= 0)
                {
                    break;
                }

                uploaded += length;
                await stream.WriteAsync(buffer.AsMemory(0, length)).ConfigureAwait(false);
                pendingProgressBytes += length;

                var currentTimestamp = Stopwatch.GetTimestamp();
                var byteThresholdReached = pendingProgressBytes >= _progressReportByteInterval;
                var timeThresholdReached =
                    Stopwatch.GetElapsedTime(lastProgressReportTimestamp, currentTimestamp) >= _progressReportMinInterval;

                if (!byteThresholdReached && !timeThresholdReached)
                {
                    continue;
                }

                _progress?.Report(new UploadProgress(uploaded, size));
                pendingProgressBytes = 0;
                lastProgressReportTimestamp = currentTimestamp;
            }

            if (pendingProgressBytes > 0 || size == 0)
            {
                _progress?.Report(new UploadProgress(uploaded, size));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _streamToWrite.Length;
        return true;
    }

    private void PrepareContent()
    {
        if (_contentConsumed)
        {
            if (_streamToWrite.CanSeek)
            {
                _streamToWrite.Position = 0;
            }
            else
            {
                throw new InvalidOperationException("The stream has already been read.");
            }
        }

        _contentConsumed = true;
    }
}
