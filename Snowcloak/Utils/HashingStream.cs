using System.Security.Cryptography;

namespace Snowcloak.Utils;

// Calculates the hash of content read or written to a stream
public class HashingStream : Stream
{
    private readonly Stream _stream;
    private readonly HashAlgorithm _hashAlgo;
    private bool _finished = false;
    public bool DisposeUnderlying { get; set; } = true;

    public Stream UnderlyingStream { get => _stream; }

    public HashingStream(Stream underlyingStream, HashAlgorithm hashAlgo)
    {
        _stream = underlyingStream;
        _hashAlgo = hashAlgo;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!DisposeUnderlying)
            return;
        if (!_finished)
            _stream.Dispose();
        _hashAlgo.Dispose();
    }

    public override bool CanRead => _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _stream.CanWrite;
    public override long Length => _stream.Length;

    public override long Position { get => _stream.Position; set => throw new NotSupportedException(); }

    public override void Flush()
    {
        _stream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        int n = _stream.Read(buffer, offset, count);
        if (n > 0)
            _hashAlgo.TransformBlock(buffer, offset, n, buffer, offset);
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _stream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _stream.Write(buffer, offset, count);
        _hashAlgo.TransformBlock(buffer, offset, count, buffer, offset);
    }

    public byte[] Finish()
    {
        if (_finished)
            return _hashAlgo.Hash!;
        _hashAlgo.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        _finished = true;
        if (DisposeUnderlying)
            _stream.Dispose();
        return _hashAlgo.Hash!;
    }
}
