using Snowcloak.API.Dto.Account;

namespace Snowcloak.Core.Accounts;

public sealed record AccountEntitlements(
    bool IsLinked = false,
    bool IsPayingPatron = false,
    bool HasBenefits = false,
    bool IsCompetitionWinner = false,
    bool IsTestOverride = false,
    bool IsCreatorForCampaign = false,
    string PatreonUserId = "")
{
    public static AccountEntitlements Empty { get; } = new();
}

public static class AccountEntitlementMapper
{
    public static AccountEntitlements FromPatreonStatus(PatreonStatusReplyDto? dto)
    {
        return dto == null
            ? AccountEntitlements.Empty
            : FromDto(dto.Entitlements, dto.IsLinked, dto.IsPayingPatron, dto.HasBenefits,
                dto.IsCompetitionWinner, dto.IsTestOverride, dto.IsCreatorForCampaign, dto.PatreonUserId);
    }

    public static AccountEntitlements FromPatreonLinkPoll(PatreonLinkPollReplyDto? dto)
    {
        return dto == null
            ? AccountEntitlements.Empty
            : FromDto(dto.Entitlements, dto.IsLinked, dto.IsPayingPatron, dto.HasBenefits,
                dto.IsCompetitionWinner, dto.IsTestOverride, dto.IsCreatorForCampaign, null);
    }

    public static AccountEntitlements FromDto(AccountEntitlementsDto? dto)
    {
        return FromDto(dto, false, false, false, false, false, false, null);
    }

    public static AccountEntitlements FromDto(AccountEntitlementsDto? dto, bool fallbackIsLinked,
        bool fallbackIsPayingPatron, bool fallbackHasBenefits, bool fallbackIsCompetitionWinner,
        bool fallbackIsTestOverride, bool fallbackIsCreatorForCampaign, string? fallbackPatreonUserId)
    {
        var hasFallback = fallbackIsLinked
                          || fallbackIsPayingPatron
                          || fallbackHasBenefits
                          || fallbackIsCompetitionWinner
                          || fallbackIsTestOverride
                          || fallbackIsCreatorForCampaign
                          || !string.IsNullOrWhiteSpace(fallbackPatreonUserId);

        if (dto != null && (HasValues(dto) || !hasFallback))
        {
            return new AccountEntitlements(
                dto.IsLinked,
                dto.IsPayingPatron,
                dto.HasBenefits,
                dto.IsCompetitionWinner,
                dto.IsTestOverride,
                dto.IsCreatorForCampaign,
                dto.PatreonUserId ?? string.Empty);
        }

        return new AccountEntitlements(
            fallbackIsLinked,
            fallbackIsPayingPatron,
            fallbackHasBenefits,
            fallbackIsCompetitionWinner,
            fallbackIsTestOverride,
            fallbackIsCreatorForCampaign,
            fallbackPatreonUserId ?? string.Empty);
    }

    private static bool HasValues(AccountEntitlementsDto dto)
    {
        return dto.IsLinked
               || dto.IsPayingPatron
               || dto.HasBenefits
               || dto.IsCompetitionWinner
               || dto.IsTestOverride
               || dto.IsCreatorForCampaign
               || !string.IsNullOrWhiteSpace(dto.PatreonUserId);
    }
}
