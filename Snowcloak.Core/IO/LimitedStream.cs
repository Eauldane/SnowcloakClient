namespace Snowcloak.Core.IO;

public sealed class LimitedStream : Stream
{
    private readonly Stream _stream;
    private long _position;

    public LimitedStream(Stream underlyingStream, long byteLimit, bool disposeUnderlying = true)
    {
        ArgumentNullException.ThrowIfNull(underlyingStream);
        ArgumentOutOfRangeException.ThrowIfNegative(byteLimit);

        _stream = underlyingStream;
        DisposeUnderlying = disposeUnderlying;
        try
        {
            _position = _stream.Position;
        }
        catch (NotSupportedException)
        {
            _position = 0;
        }

        MaxPosition = _position + byteLimit;
    }

    public long MaxPosition { get; }
    public bool DisposeUnderlying { get; set; }
    public Stream UnderlyingStream => _stream;
    public override bool CanRead => _stream.CanRead;
    public override bool CanSeek => _stream.CanSeek;
    public override bool CanWrite => _stream.CanWrite;
    public override long Length => _stream.Length;

    public override long Position
    {
        get => _stream.Position;
        set
        {
            _stream.Position = value;
            _position = value;
        }
    }

    public override void Flush() => _stream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var limitedCount = ClampCount(count);
        var read = _stream.Read(buffer, offset, limitedCount);
        _position += read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var limitedCount = ClampCount(count);
        var read = await _stream.ReadAsync(buffer.AsMemory(offset, limitedCount), cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var limitedBuffer = buffer[..ClampCount(buffer.Length)];
        var read = await _stream.ReadAsync(limitedBuffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var result = _stream.Seek(offset, origin);
        _position = result;
        return result;
    }

    public override void SetLength(long value) => _stream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        var limitedCount = ClampCount(count);
        _stream.Write(buffer, offset, limitedCount);
        _position += limitedCount;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var limitedCount = ClampCount(count);
        await _stream.WriteAsync(buffer.AsMemory(offset, limitedCount), cancellationToken).ConfigureAwait(false);
        _position += limitedCount;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var limitedBuffer = buffer[..ClampCount(buffer.Length)];
        await _stream.WriteAsync(limitedBuffer, cancellationToken).ConfigureAwait(false);
        _position += limitedBuffer.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && DisposeUnderlying)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }

    private int ClampCount(int count)
    {
        var remaining = Math.Clamp(MaxPosition - _position, 0, int.MaxValue);
        return Math.Min(count, (int)remaining);
    }
}
