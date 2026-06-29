using Snowcloak.API.Dto.Venue;
using Snowcloak.Services.Venue;

namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
public record OpenVenueSyncshellPopupMessage(VenueSyncshellPrompt Prompt) : SameThreadMessage;
public record VenueSyncshellJoinAcceptedMessage(VenueSyncshellDto Venue, VenueLocationDto Location) : MessageBase;
public record OpenVenueRegistrationWindowMessage(VenueRegistrationContext Context) : SameThreadMessage;
public record OpenVenueRegistryWindowMessage : SameThreadMessage;
public record OpenVenueAdsWindowMessage(bool OpenCreate) : SameThreadMessage;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
