using System.Text.Json;

namespace Snowcloak.Core.EnvironmentSnapshots;

public static class PersistentDocumentCodec
{
    private const string Marker = "SnowcloakOpaqueDocumentV1";
    public static string EncodeBytes(byte[] bytes) => JsonSerializer.Serialize(new Dictionary<string, string> { [Marker] = Convert.ToBase64String(bytes) });
    public static bool IsOpaque(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.EnumerateObject().Count() == 1
            && document.RootElement.TryGetProperty(Marker, out var value) && value.ValueKind == JsonValueKind.String;
    }
    public static byte[] DecodeBytes(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Convert.FromBase64String(document.RootElement.GetProperty(Marker).GetString()!);
    }
}
