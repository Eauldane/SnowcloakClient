using Microsoft.Extensions.DependencyInjection;
using Snowcloak.Game.Housing;
using Snowcloak.Services.Venue;
using Snowcloak.UI.Components.Popup;

namespace Snowcloak.Initialization;

internal static class VenueServiceRegistration
{
    public static IServiceCollection AddSnowcloakVenue(this IServiceCollection collection)
    {
        collection.AddSingleton<HousingPlacardReader>();
        collection.AddSingleton<VenueSyncshellService>();
        collection.AddSingleton<VenueRegistrationService>();
        collection.AddSingleton<VenueReminderService>();

        collection.AddScoped<IPopupHandler, VenueSyncshellPopupHandler>();

        return collection;
    }
}
