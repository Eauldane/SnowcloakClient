using System.Security.Cryptography;

namespace Snowcloak.Core.IO;

public sealed class HashingStream : Stream
{
    private readonly Stream _stream;
    private readonly HashAlgorithm _hashAlgorithm;
    private bool _finished;

    public HashingStream(Stream underlyingStream, HashAlgorithm hashAlgorithm, bool disposeUnderlying = true)
    {
        _stream = underlyingStream ?? throw new ArgumentNullException(nameof(underlyingStream));
        _hashAlgorithm = hashAlgorithm ?? throw new ArgumentNullException(nameof(hashAlgorithm));
        DisposeUnderlying = disposeUnderlying;
    }

    public bool DisposeUnderlying { get; set; }
    public Stream UnderlyingStream => _stream;
    public override bool CanRead => !_finished && _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => !_finished && _stream.CanWrite;
    public override long Length => _stream.Length;
    public override long Position { get => _stream.Position; set => throw new NotSupportedException(); }

    public override void Flush() => _stream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        var read = _stream.Read(buffer, offset, count);
        if (read > 0)
        {
            _hashAlgorithm.TransformBlock(buffer, offset, read, null, 0);
        }

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _stream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _stream.Write(buffer, offset, count);
        if (count > 0)
        {
            _hashAlgorithm.TransformBlock(buffer, offset, count, null, 0);
        }
    }

    public byte[] Finish()
    {
        if (!_finished)
        {
            _hashAlgorithm.TransformFinalBlock([], 0, 0);
            _finished = true;
            if (DisposeUnderlying)
            {
                _stream.Dispose();
            }
        }

        return _hashAlgorithm.Hash ?? [];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!_finished && DisposeUnderlying)
            {
                _stream.Dispose();
            }

            _hashAlgorithm.Dispose();
        }

        base.Dispose(disposing);
    }
}
