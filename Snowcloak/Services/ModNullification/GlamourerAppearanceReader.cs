using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Snowcloak.Services.ModNullification;

[StructLayout(LayoutKind.Auto)]
public readonly record struct GlamourerAppearance(byte? Gender, byte? Race, byte? Clan, byte? Height);

public static class GlamourerAppearanceReader
{
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
            var gzipStart = Array.IndexOf(rawBytes, (byte)0x1F);
            if (gzipStart < 0 || gzipStart + 1 >= rawBytes.Length || rawBytes[gzipStart + 1] != 0x8B)
            {
                return false;
            }

            using var memory = new MemoryStream(rawBytes, gzipStart, rawBytes.Length - gzipStart);
            using var gzip = new GZipStream(memory, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            using var document = JsonDocument.Parse(reader.ReadToEnd());

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
        catch
        {
            return false;
        }
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
