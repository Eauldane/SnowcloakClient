using System.Globalization;
using System.Text;

namespace Snowcloak.Core.Files;

public readonly record struct BlockFileEntry(string Hash, long Length, long DataOffset);

public static class BlockFileFormat
{
    private const char EntryMarker = '#';
    private const char LengthSeparator = ':';

    public static int HeaderLength(string hash, long length)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return hash.Length + length.ToString(CultureInfo.InvariantCulture).Length + 3;
    }

    public static long EntryLength(string hash, long length) => length + HeaderLength(hash, length);

    public static void WriteHeader(Stream stream, string hash, long length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(hash);

        var header = EntryMarker + hash + LengthSeparator + length.ToString(CultureInfo.InvariantCulture) + EntryMarker;
        var bytes = Encoding.ASCII.GetBytes(header);
        stream.Write(bytes, 0, bytes.Length);
    }

    public static BlockFileEntry ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var separator = (char)ReadByteOrThrow(stream);
        if (separator != EntryMarker)
        {
            throw new InvalidDataException("Block file is invalid, first character is not a marker.");
        }

        var hash = new StringBuilder();
        var length = new StringBuilder();
        var readingLength = false;

        while (true)
        {
            var readChar = (char)ReadByteOrThrow(stream);
            if (readChar == LengthSeparator)
            {
                readingLength = true;
                continue;
            }

            if (readChar == EntryMarker)
            {
                break;
            }

            if (readingLength)
            {
                length.Append(readChar);
            }
            else
            {
                hash.Append(readChar);
            }
        }

        var lengthBytes = length.Length == 0
            ? 0
            : long.Parse(length.ToString(), CultureInfo.InvariantCulture);

        return new BlockFileEntry(hash.ToString(), lengthBytes, stream.Position);
    }

    public static IEnumerable<BlockFileEntry> ReadEntries(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("Reading block file entries requires a seekable stream.", nameof(stream));
        }

        return Enumerate(stream);

        static IEnumerable<BlockFileEntry> Enumerate(Stream stream)
        {
            while (stream.Position < stream.Length)
            {
                var entry = ReadHeader(stream);
                stream.Position = entry.DataOffset + entry.Length;
                yield return entry;
            }
        }
    }

    private static int ReadByteOrThrow(Stream stream)
    {
        var value = stream.ReadByte();
        if (value == -1)
        {
            throw new EndOfStreamException();
        }

        return value;
    }
}
