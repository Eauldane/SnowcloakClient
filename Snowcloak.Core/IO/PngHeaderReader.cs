using System.Buffers.Binary;

namespace Snowcloak.Core.IO;

public static class PngHeaderReader
{
    private static readonly byte[] MagicSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] Ihdr = [(byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    public static readonly (int Width, int Height) InvalidSize = (0, 0);

    public static (int Width, int Height) TryExtractDimensions(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buffer = stackalloc byte[8];

        try
        {
            stream.ReadExactly(buffer);
            if (!buffer.SequenceEqual(MagicSignature))
            {
                return InvalidSize;
            }

            stream.ReadExactly(buffer);
            var ihdrLength = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
            if (ihdrLength < 8 || !buffer[4..].SequenceEqual(Ihdr))
            {
                return InvalidSize;
            }

            stream.ReadExactly(buffer);
            var width = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..]);

            if (width > int.MaxValue || height > int.MaxValue)
            {
                return InvalidSize;
            }

            return ((int)width, (int)height);
        }
        catch (EndOfStreamException)
        {
            return InvalidSize;
        }
    }
}
