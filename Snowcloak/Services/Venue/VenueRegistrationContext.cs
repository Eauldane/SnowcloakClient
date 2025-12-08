using Snowcloak.Services.Housing;

namespace Snowcloak.Services.Venue;

public sealed record VenueRegistrationContext(
    HousingPlotLocation Location,
    string? OwnerName,
    string? FreeCompanyTag,
    bool AuthorisedByFreeCompany)
{
    public bool IsFreeCompanyPlot => !string.IsNullOrWhiteSpace(FreeCompanyTag);
}