using Microsoft.Extensions.DependencyInjection;
using Scrutor;
using Snowcloak.Interop.Ipc;

namespace Snowcloak.Initialization;

internal static class IpcServiceRegistration
{
    public static IServiceCollection AddSnowcloakIpc(this IServiceCollection collection)
    {

        collection.Scan(scan => scan
            .FromAssemblyOf<IpcManager>()
            .AddClasses(classes => classes.AssignableTo<IIpcCaller>(), publicOnly: false)
            .AsSelf()
            .WithSingletonLifetime());

        collection.AddSingleton<IpcTraceRecorder>();

#if DEBUG
        collection.AddSingleton<IPenumbraIpc>(sp => new RecordingPenumbraIpc(sp.GetRequiredService<IpcCallerPenumbra>(), sp.GetRequiredService<IpcTraceRecorder>()));
        collection.AddSingleton<IGlamourerIpc>(sp => new RecordingGlamourerIpc(sp.GetRequiredService<IpcCallerGlamourer>(), sp.GetRequiredService<IpcTraceRecorder>()));
        collection.AddSingleton<ICustomizePlusIpc>(sp => new RecordingCustomizePlusIpc(sp.GetRequiredService<IpcCallerCustomize>(), sp.GetRequiredService<IpcTraceRecorder>()));
        collection.AddSingleton<IHeelsIpc>(sp => new RecordingHeelsIpc(sp.GetRequiredService<IpcCallerHeels>(), sp.GetRequiredService<IpcTraceRecorder>()));
        collection.AddSingleton<IHonorificIpc>(sp => new RecordingHonorificIpc(sp.GetRequiredService<IpcCallerHonorific>(), sp.GetRequiredService<IpcTraceRecorder>()));
        collection.AddSingleton<IMoodlesIpc>(sp => new RecordingMoodlesIpc(sp.GetRequiredService<IpcCallerMoodles>(), sp.GetRequiredService<IpcTraceRecorder>()));
        collection.AddSingleton<IPetNamesIpc>(sp => new RecordingPetNamesIpc(sp.GetRequiredService<IpcCallerPetNames>(), sp.GetRequiredService<IpcTraceRecorder>()));
#else
        collection.AddSingleton<IPenumbraIpc>(sp => sp.GetRequiredService<IpcCallerPenumbra>());
        collection.AddSingleton<IGlamourerIpc>(sp => sp.GetRequiredService<IpcCallerGlamourer>());
        collection.AddSingleton<ICustomizePlusIpc>(sp => sp.GetRequiredService<IpcCallerCustomize>());
        collection.AddSingleton<IHeelsIpc>(sp => sp.GetRequiredService<IpcCallerHeels>());
        collection.AddSingleton<IHonorificIpc>(sp => sp.GetRequiredService<IpcCallerHonorific>());
        collection.AddSingleton<IMoodlesIpc>(sp => sp.GetRequiredService<IpcCallerMoodles>());
        collection.AddSingleton<IPetNamesIpc>(sp => sp.GetRequiredService<IpcCallerPetNames>());
#endif

        collection.AddSingleton<IpcCallerSnow>();
        collection.AddSingleton<IpcManager>();
        collection.AddSingleton<RedrawManager>();
        collection.AddSingleton<IpcProvider>();

        return collection;
    }
}
