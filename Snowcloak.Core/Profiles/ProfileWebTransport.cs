using System.Buffers.Binary;

namespace Snowcloak.Core.Profiles;

public static class ProfileWebTransport
{
    public static async Task<byte[]?> ReadAsync(Stream stream, int limit, CancellationToken token)
    {
        var header = new byte[4];
        var read = await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
        if (read == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > limit) throw new InvalidDataException("Web editor message limit exceeded.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    public static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
