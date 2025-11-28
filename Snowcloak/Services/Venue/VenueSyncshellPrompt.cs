using Snowcloak.API.Dto.Venue;
using Snowcloak.Services.Housing;

namespace Snowcloak.Services.Venue;

public sealed class VenueSyncshellPrompt
{
    public VenueSyncshellPrompt(VenueSyncshellDto venue, HousingPlotLocation location)
    {
        PromptId = Guid.NewGuid();
        Venue = venue;
        Location = location;
    }

    public Guid PromptId { get; }

    public VenueSyncshellDto Venue { get; }

    public HousingPlotLocation Location { get; }
}