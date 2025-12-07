using Snowcloak.Services.Housing;

namespace Snowcloak.Services.Venue;

public sealed record VenueRegistrationContext(
    HousingPlotLocation Location,
    string? OwnerName,
    string? FreeCompanyTag,
    bool AuthorizedByFreeCompany)
{
    public bool IsFreeCompanyPlot => !string.IsNullOrWhiteSpace(FreeCompanyTag);
}