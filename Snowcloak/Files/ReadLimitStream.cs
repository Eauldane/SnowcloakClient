namespace Snowcloak.Files;

internal sealed class ReadLimitStream(Stream inner, long maxBytes) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private long _remainingBytes = maxBytes >= 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));

    public long RemainingBytes => _remainingBytes;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remainingBytes <= 0) return 0;

        int toRead = (int)Math.Min(count, _remainingBytes);
        int read = _inner.Read(buffer, offset, toRead);
        _remainingBytes -= read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_remainingBytes <= 0) return 0;

        int toRead = (int)Math.Min(buffer.Length, _remainingBytes);
        int read = _inner.Read(buffer[..toRead]);
        _remainingBytes -= read;
        return read;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remainingBytes <= 0) return ValueTask.FromResult(0);

        int toRead = (int)Math.Min(buffer.Length, _remainingBytes);
        return ReadBoundedAsync(buffer[..toRead], cancellationToken);
    }

    private async ValueTask<int> ReadBoundedAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _remainingBytes -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_remainingBytes <= 0) return Task.FromResult(0);

        int toRead = (int)Math.Min(count, _remainingBytes);
        return ReadBoundedAsync(new Memory<byte>(buffer, offset, toRead), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
}
