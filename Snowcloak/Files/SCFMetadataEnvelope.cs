using System.Collections.ObjectModel;

namespace Snowcloak.Files;

public static class SCFMetadataEnvelope
{
    public const byte EnvelopeVersion = 1;

    public static byte[] Create(IReadOnlyDictionary<byte, byte[]>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return [];
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(EnvelopeVersion);
        bw.Write((ushort)fields.Count);

        foreach (var (fieldId, value) in fields)
        {
            var payload = value ?? throw new ArgumentNullException(nameof(fields), $"SCF: Metadata field {fieldId} has null value.");
            bw.Write((byte)fieldId);
            bw.Write((ushort)payload.Length);
            bw.Write(payload);
        }

        return ms.ToArray();
    }


    public static IReadOnlyDictionary<byte, byte[]> Parse(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length == 0)
        {
            return new ReadOnlyDictionary<byte, byte[]>(new Dictionary<byte, byte[]>());
        }

        using var ms = new MemoryStream(envelope.ToArray(), writable: false);
        using var br = new BinaryReader(ms);

        var version = br.ReadByte();
        if (version != EnvelopeVersion)
        {
            throw new InvalidDataException($"SCF: Unsupported metadata envelope version {version}.");
        }

        var fieldCount = br.ReadUInt16();
        var result = new Dictionary<byte, byte[]>(fieldCount);

        for (var i = 0; i < fieldCount; i++)
        {
            var fieldId = br.ReadByte();
            var fieldLength = br.ReadUInt16();
            var value = br.ReadBytes(fieldLength);
            if (value.Length != fieldLength)
            {
                throw new EndOfStreamException("SCF: Unexpected end of stream while reading metadata envelope field.");
            }

            result[fieldId] = value;
        }

        if (ms.Position != ms.Length)
        {
            throw new InvalidDataException("SCF: Metadata envelope contains trailing bytes.");
        }

        return new ReadOnlyDictionary<byte, byte[]>(result);
    }

}
