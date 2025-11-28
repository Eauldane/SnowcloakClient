using MessagePack;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Blake3;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.API.Data;

[MessagePackObject(keyAsPropertyName: true)]
public class CharacterData
{
    public CharacterData()
    {
        DataHash = new(() =>
        {
            var json = JsonSerializer.Serialize(this);
            var hash = Hasher.Hash(Encoding.UTF8.GetBytes(json));
            return hash.ToString().ToUpperInvariant();
        });
    }

    public Dictionary<ObjectKind, string> CustomizePlusData { get; set; } = new();
    [JsonIgnore]
    public Lazy<string> DataHash { get; }

    public Dictionary<ObjectKind, List<FileReplacementData>> FileReplacements { get; set; } = new();
    public Dictionary<ObjectKind, string> GlamourerData { get; set; } = new();
    public string HeelsData { get; set; } = string.Empty;
    public string HonorificData { get; set; } = string.Empty;
    public string ManipulationData { get; set; } = string.Empty;
    public string MoodlesData { get; set; } = string.Empty;
    public string PetNamesData { get; set; } = string.Empty;
}