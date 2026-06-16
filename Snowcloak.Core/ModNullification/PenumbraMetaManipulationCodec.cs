using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Snowcloak.Core.ModNullification;

public enum RspModifier
{
    MaleMinSize,
    MaleMaxSize,
    MaleMinTail,
    MaleMaxTail,
    FemaleMinSize,
    FemaleMaxSize,
    FemaleMinTail,
    FemaleMaxTail,
    BustMinX,
    BustMinY,
    BustMinZ,
    BustMaxX,
    BustMaxY,
    BustMaxZ,
}

public readonly record struct RspIdentifier(byte Clan, RspModifier Modifier);

public static class PenumbraMetaManipulationCodec
{
    private static readonly byte[] VersionOneHeader = Encoding.ASCII.GetBytes("META0001");
    private static readonly int[] BlocksBeforeRspEntrySizes =
    [
        8 + 6,
        4 + 8,
        6 + 2,
        6 + 2,
    ];

    private const byte SupportedVersion = 1;
    private const int PrefixLength = 1 + 8;
    private const int RspIdentifierSize = 2;
    private const int RspEntrySize = RspIdentifierSize + sizeof(float);
    private const int MaxCompressedBytes = 4 * 1024 * 1024;
    private const int MaxDecodedBytes = 16 * 1024 * 1024;

    public static bool TryReadHeightEntries(string? manipulationData, out IReadOnlyDictionary<RspIdentifier, float> entries)
    {
        entries = new Dictionary<RspIdentifier, float>();
        if (string.IsNullOrEmpty(manipulationData))
        {
            return true;
        }

        if (!TryDecompress(manipulationData, out var data)
            || !TryFindRspBlock(data, out _, out var rspEntriesOffset, out var rspCount, out _))
        {
            return false;
        }

        var result = new Dictionary<RspIdentifier, float>();
        var offset = rspEntriesOffset;
        for (var index = 0; index < rspCount; index++)
        {
            var identifier = ReadIdentifier(data, offset);
            var value = BitConverter.ToSingle(data, offset + RspIdentifierSize);
            if (IsHeightModifier(identifier.Modifier))
            {
                result[identifier] = value;
            }

            offset += RspEntrySize;
        }

        entries = result;
        return true;
    }

    public static bool TryRemoveHeightEntries(string? manipulationData, out string filteredManipulationData, out int removedCount)
    {
        filteredManipulationData = manipulationData ?? string.Empty;
        removedCount = 0;
        if (string.IsNullOrEmpty(manipulationData))
        {
            return true;
        }

        if (!TryDecompress(manipulationData, out var data)
            || !TryFindRspBlock(data, out var rspCountOffset, out var rspEntriesOffset, out var rspCount, out var rspEndOffset))
        {
            return false;
        }

        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, rspCountOffset);

        var retainedEntries = new List<ArraySegment<byte>>(rspCount);
        var offset = rspEntriesOffset;
        for (var index = 0; index < rspCount; index++)
        {
            var identifier = ReadIdentifier(data, offset);
            if (IsHeightModifier(identifier.Modifier))
            {
                removedCount++;
            }
            else
            {
                retainedEntries.Add(new ArraySegment<byte>(data, offset, RspEntrySize));
            }

            offset += RspEntrySize;
        }

        if (removedCount == 0)
        {
            return true;
        }

        Span<byte> countBuffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(countBuffer, retainedEntries.Count);
        output.Write(countBuffer);
        foreach (var entry in retainedEntries)
        {
            output.Write(entry.Array!, entry.Offset, entry.Count);
        }

        output.Write(data, rspEndOffset, data.Length - rspEndOffset);
        filteredManipulationData = Compress(output.ToArray());
        return true;
    }

    private static bool TryFindRspBlock(byte[] data, out int rspCountOffset, out int rspEntriesOffset, out int rspCount, out int rspEndOffset)
    {
        rspCountOffset = 0;
        rspEntriesOffset = 0;
        rspCount = 0;
        rspEndOffset = 0;

        if (data.Length < PrefixLength
            || data[0] != SupportedVersion
            || !data.AsSpan(1, VersionOneHeader.Length).SequenceEqual(VersionOneHeader))
        {
            return false;
        }

        var offset = PrefixLength;
        foreach (var entrySize in BlocksBeforeRspEntrySizes)
        {
            if (!TrySkipBlock(data, ref offset, entrySize))
            {
                return false;
            }
        }

        rspCountOffset = offset;
        if (!TryReadCount(data, ref offset, out rspCount)
            || !TryAdvance(data, ref offset, rspCount, RspEntrySize))
        {
            return false;
        }

        rspEntriesOffset = rspCountOffset + sizeof(int);
        rspEndOffset = offset;
        return true;
    }

    private static bool TrySkipBlock(byte[] data, ref int offset, int entrySize)
    {
        return TryReadCount(data, ref offset, out var count)
            && TryAdvance(data, ref offset, count, entrySize);
    }

    private static bool TryReadCount(byte[] data, ref int offset, out int count)
    {
        count = 0;
        if (offset < 0 || offset + sizeof(int) > data.Length)
        {
            return false;
        }

        count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return count >= 0;
    }

    private static bool TryAdvance(byte[] data, ref int offset, int count, int entrySize)
    {
        try
        {
            var length = checked(count * entrySize);
            var nextOffset = checked(offset + length);
            if (nextOffset > data.Length)
            {
                return false;
            }

            offset = nextOffset;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static RspIdentifier ReadIdentifier(byte[] data, int offset)
    {
        return new RspIdentifier(data[offset], (RspModifier)data[offset + 1]);
    }

    private static bool IsHeightModifier(RspModifier modifier)
    {
        return modifier is RspModifier.MaleMinSize
            or RspModifier.MaleMaxSize
            or RspModifier.FemaleMinSize
            or RspModifier.FemaleMaxSize;
    }

    private static bool TryDecompress(string manipulationData, out byte[] data)
    {
        data = [];
        try
        {
            var compressed = Convert.FromBase64String(manipulationData);
            if (compressed.Length > MaxCompressedBytes)
            {
                return false;
            }

            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaxDecodedBytes)
                {
                    return false;
                }

                output.Write(buffer, 0, read);
            }

            data = output.ToArray();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return Convert.ToBase64String(output.ToArray());
    }
}
