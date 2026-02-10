using System.Collections.ObjectModel;
using System.Text;

namespace Snowcloak.Files;

public static class SCFMetadataEnvelope
{
    public const byte EnvelopeVersion = 1;

    public static byte[] Create(IReadOnlyDictionary<string, byte[]>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return [];
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(EnvelopeVersion);
        bw.Write((ushort)fields.Count);

        foreach (var (fieldKey, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(fieldKey))
            {
                throw new ArgumentException("SCF: Metadata field key cannot be null or empty.", nameof(fields));
            }

            var keyBytes = Encoding.UTF8.GetBytes(fieldKey);
            if (keyBytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException($"SCF: Metadata field key '{fieldKey}' is too large.", nameof(fields));
            }

            var payload = value ?? throw new ArgumentNullException(nameof(fields), $"SCF: Metadata field '{fieldKey}' has null value.");
            if (payload.Length > ushort.MaxValue)
            {
                throw new ArgumentException($"SCF: Metadata field '{fieldKey}' payload is too large.", nameof(fields));
            }

            bw.Write((ushort)keyBytes.Length);
            bw.Write(keyBytes);
            bw.Write((ushort)payload.Length);
            bw.Write(payload);
        }

        return ms.ToArray();
    }


    public static IReadOnlyDictionary<string, byte[]> Parse(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length == 0)
        {
            return new ReadOnlyDictionary<string, byte[]>(new Dictionary<string, byte[]>());
        }

        using var ms = new MemoryStream(envelope.ToArray(), writable: false);
        using var br = new BinaryReader(ms);

        var version = br.ReadByte();
        if (version != EnvelopeVersion)
        {
            throw new InvalidDataException($"SCF: Unsupported metadata envelope version {version}.");
        }

        var fieldCount = br.ReadUInt16();
        var result = new Dictionary<string, byte[]>(fieldCount);

        for (var i = 0; i < fieldCount; i++)
        {
            var keyLength = br.ReadUInt16();
            var keyBytes = br.ReadBytes(keyLength);
            if (keyBytes.Length != keyLength)
            {
                throw new EndOfStreamException("SCF: Unexpected end of stream while reading metadata envelope field key.");
            }

            var key = Encoding.UTF8.GetString(keyBytes);
            var fieldLength = br.ReadUInt16();
            var value = br.ReadBytes(fieldLength);
            if (value.Length != fieldLength)
            {
                throw new EndOfStreamException("SCF: Unexpected end of stream while reading metadata envelope field.");
            }

            result[key] = value;
        }

        if (ms.Position != ms.Length)
        {
            throw new InvalidDataException("SCF: Metadata envelope contains trailing bytes.");
        }

        return new ReadOnlyDictionary<string, byte[]>(result);
    }

}
