namespace Snowcloak.EnvironmentSnapshots;

internal sealed class SnapshotUploadStream(Stream source, Func<CancellationToken, Task> wait, CancellationToken operation, Action<int>? onRead = null) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => source.Position; set => source.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) { wait(operation).GetAwaiter().GetResult(); operation.ThrowIfCancellationRequested(); var read = source.Read(buffer, offset, count); onRead?.Invoke(read); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operation, cancellationToken);
        await wait(linked.Token).ConfigureAwait(false);
        var read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        onRead?.Invoke(read); return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
}
