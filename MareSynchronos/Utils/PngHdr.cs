namespace MareSynchronos.Utils;

public class PngHdr
{
	private static readonly byte[] _magicSignature = [137, 80, 78, 71, 13, 10, 26, 10];
	private static readonly byte[] _IHDR = [(byte)'I', (byte)'H', (byte)'D', (byte)'R'];
	public static readonly (int Width, int Height) InvalidSize = (0, 0);

	public static (int Width, int Height) TryExtractDimensions(Stream stream)
	{
		Span<byte> buffer = stackalloc byte[8];

		try
		{
			stream.ReadExactly(buffer[..8]);

			// All PNG files start with the same 8 bytes
			if (!buffer.SequenceEqual(_magicSignature))
				return InvalidSize;

			stream.ReadExactly(buffer[..8]);

			uint ihdrLength = ReadBigEndianUInt32(buffer[..4]);

			// The next four bytes will be the length of the IHDR section (it should be 13 bytes but we only need 8)
			if (ihdrLength < 8)
				return InvalidSize;

			// followed by ASCII "IHDR"
			if (!buffer[4..].SequenceEqual(_IHDR))
				return InvalidSize;

			stream.ReadExactly(buffer[..8]);

			uint width = ReadBigEndianUInt32(buffer[..4]);
			uint height = ReadBigEndianUInt32(buffer[4..8]);

			// Validate the width/height are non-negative and... that's all we care about!
			if (width > int.MaxValue || height > int.MaxValue)
				return InvalidSize;

			return ((int)width, (int)height);
		}
		catch (EndOfStreamException)
		{
			return InvalidSize;
		}
	}
    // Minimal helper for big-endian conversion
    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> bytes)
    {
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }
}