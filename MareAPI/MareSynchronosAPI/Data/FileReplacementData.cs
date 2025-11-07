using MessagePack;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Blake3;

namespace MareSynchronos.API.Data;

[MessagePackObject(keyAsPropertyName: true)]
public class FileReplacementData
{
    public FileReplacementData()
    {
        DataHash = new(() =>
        {
            var json = JsonSerializer.Serialize(this);
            var hash = Hasher.Hash(Encoding.UTF8.GetBytes(json));
            return hash.ToString().ToUpperInvariant();
        });
    }

    [JsonIgnore]
    public Lazy<string> DataHash { get; }
    public string[] GamePaths { get; set; } = Array.Empty<string>();
    public string Hash { get; set; } = string.Empty;
    public string FileSwapPath { get; set; } = string.Empty;
}