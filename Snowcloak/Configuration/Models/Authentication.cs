namespace Snowcloak.Configuration.Models;

[Serializable]
public record Authentication
{
    public string CharacterName { get; set; } = string.Empty;
    public uint WorldId { get; set; } = 0;
    public int SecretKeyIdx { get; set; } = -1;
    public ulong ContentId { get; set; }
    public Guid? IdentityBindingId { get; set; }
    public List<string> PendingLegacyIdents { get; set; } = [];
}
