using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Snowcloak.Core.ModNullification;

public readonly record struct GlamourerAppearance(byte? Gender, byte? Race, byte? Clan, byte? Height);

public static class GlamourerAppearanceReader
{
    private const int MaxEnvelopeBytes = 4 * 1024 * 1024;
    private const int MaxGzipPrefixBytes = 512;
    private const int MaxJsonBytes = 4 * 1024 * 1024;

    public static bool TryRead(string? glamourerStateBase64, out GlamourerAppearance appearance)
    {
        appearance = default;
        if (string.IsNullOrWhiteSpace(glamourerStateBase64))
        {
            return false;
        }

        try
        {
            var rawBytes = Convert.FromBase64String(glamourerStateBase64);
            if (rawBytes.Length == 0 || rawBytes.Length > MaxEnvelopeBytes)
            {
                return false;
            }

            var gzipStart = FindGzipHeader(rawBytes);
            if (gzipStart < 0)
            {
                return false;
            }

            using var memory = new MemoryStream(rawBytes, gzipStart, rawBytes.Length - gzipStart);
            using var gzip = new GZipStream(memory, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaxJsonBytes)
                {
                    return false;
                }

                output.Write(buffer, 0, read);
            }

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(output.ToArray()));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Customize", out var customize)
                || customize.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            appearance = new GlamourerAppearance(
                ExtractByteFrom(customize, "Gender"),
                ExtractByteFrom(customize, "Race"),
                ExtractByteFrom(customize, "Clan"),
                ExtractByteFrom(customize, "Height"));

            return appearance.Clan.HasValue && appearance.Gender.HasValue && appearance.Height.HasValue;
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
        catch (JsonException)
        {
            return false;
        }
    }

    private static int FindGzipHeader(byte[] rawBytes)
    {
        var searchLength = Math.Min(rawBytes.Length - 1, MaxGzipPrefixBytes);
        for (var index = 0; index < searchLength; index++)
        {
            if (rawBytes[index] == 0x1F && rawBytes[index + 1] == 0x8B)
            {
                return index;
            }
        }

        return -1;
    }

    private static byte? ExtractByteFrom(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        return ExtractByteValue(element);
    }

    private static byte? ExtractByteValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Value", out var valueElement))
        {
            return ExtractByteValue(valueElement);
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetByte(out var numberValue))
        {
            return numberValue;
        }

        if (element.ValueKind == JsonValueKind.String && byte.TryParse(element.GetString(), out var parsedValue))
        {
            return parsedValue;
        }

        return null;
    }
}
