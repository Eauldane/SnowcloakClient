using Microsoft.Extensions.DependencyInjection;
using Snowcloak.Services.CharaData;

namespace Snowcloak.Initialization;

internal static class CharaDataServiceRegistration
{
    public static IServiceCollection AddSnowcloakCharaData(this IServiceCollection collection)
    {
        collection.AddSingleton<CharaDataManager>();
        collection.AddSingleton<MetaInfoCache>();
        collection.AddSingleton<OwnCharaDataStore>();
        collection.AddSingleton<SharedCharaDataStore>();
        collection.AddSingleton<CharaDataApplicationService>();
        collection.AddSingleton<McdfService>();
        collection.AddSingleton<CharaDataFileHandler>();
        collection.AddSingleton<CharaDataCharacterHandler>();
        collection.AddSingleton<CharaDataNearbyManager>();
        collection.AddSingleton<GposeLobbySession>();

        return collection;
    }
}
