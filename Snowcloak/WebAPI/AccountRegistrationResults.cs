using Snowcloak.Core.Accounts;

namespace Snowcloak.WebAPI;

public sealed record PatreonStatusResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public AccountEntitlements Entitlements { get; set; } = AccountEntitlements.Empty;
}

public sealed record PatreonLoginResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public AccountEntitlements Entitlements { get; set; } = AccountEntitlements.Empty;
}

public sealed record AccountOperationResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string Uid { get; set; } = string.Empty;
    public Guid? UserAccountId { get; set; }
    public int SecretKeyCount { get; set; }
    public int NewSecretKeyCount { get; set; }
    public int LinkedLocalSecretKeyCount { get; set; }
}
