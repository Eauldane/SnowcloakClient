using MessagePack;

namespace Snowcloak.API.Dto.Account;

[MessagePackObject(keyAsPropertyName: true)]
public record RegisterReplyV2Dto
{
    public bool Success { get; set; } = false;
    public string ErrorMessage { get; set; } = string.Empty;
    public string UID { get; set; } = string.Empty;
}