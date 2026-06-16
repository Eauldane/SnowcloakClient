using Microsoft.Extensions.Logging;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.PlayerData.Data;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Interop.Ipc;

public sealed partial class IpcManager : DisposableMediatorSubscriberBase
{
    public const string CustomizePlusIpcName = "CustomizePlus";
    public const string HeelsIpcName = "Heels";
    public const string HonorificIpcName = "Honorific";
    public const string MoodlesIpcName = "Moodles";
    public const string PetNamesIpcName = "PetNames";

    private readonly Dictionary<string, IpcStatus> _statusByName = new(StringComparer.Ordinal);
    private readonly IIpcCaller[] _ipcCallers;
    private readonly IFrameTickHandle _tick;

    public IpcManager(ILogger<IpcManager> logger, SnowMediator mediator,
        IPenumbraIpc penumbraIpc, IGlamourerIpc glamourerIpc, ICustomizePlusIpc customizeIpc, IHeelsIpc heelsIpc,
        IHonorificIpc honorificIpc, IMoodlesIpc moodlesIpc, IPetNamesIpc ipcCallerPetNames, IpcCallerBrio ipcCallerBrio,
        IFrameScheduler frameScheduler) : base(logger, mediator)
    {
        CustomizePlus = customizeIpc;
        Heels = heelsIpc;
        Glamourer = glamourerIpc;
        Penumbra = penumbraIpc;
        Honorific = honorificIpc;
        Moodles = moodlesIpc;
        PetNames = ipcCallerPetNames;
        Brio = ipcCallerBrio;
        _ipcCallers =
        [
            Penumbra,
            Glamourer,
            CustomizePlus,
            Heels,
            Honorific,
            Moodles,
            PetNames,
            Brio,
        ];

        if (Initialized)
        {
            Mediator.Publish(new PenumbraInitializedMessage());
        }

        _tick = frameScheduler.Register("IpcApiCheck", TickInterval.EveryMilliseconds(200), TickPriority.High, PeriodicApiStateCheck,
            FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);

        try
        {
            PeriodicApiStateCheck();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check for some IPC, plugin not installed?");
        }
    }

    protected override void Dispose(bool disposing)
    {
        _tick.Dispose();
        base.Dispose(disposing);
    }

    public bool Initialized => Penumbra.APIAvailable && Glamourer.APIAvailable;

    public ICustomizePlusIpc CustomizePlus { get; init; }
    public IHonorificIpc Honorific { get; init; }
    public IHeelsIpc Heels { get; init; }
    public IGlamourerIpc Glamourer { get; }
    public IPenumbraIpc Penumbra { get; }
    public IMoodlesIpc Moodles { get; }
    public IPetNamesIpc PetNames { get; }

    public IpcCallerBrio Brio { get; }

    public IReadOnlyList<IpcStatus> GetStatuses()
        => _ipcCallers.Select(caller => caller.Status).ToArray();

    public IReadOnlyList<IpcStatus> GetRequiredStatuses()
        => GetStatuses().Where(status => status.Role == IpcRole.Required).ToArray();

    public IReadOnlyList<IpcStatus> GetOptionalStatuses()
        => GetStatuses().Where(status => status.Role == IpcRole.Optional).ToArray();

    public IpcStatus GetStatus(string ipcName)
        => _statusByName.TryGetValue(ipcName, out var status)
            ? status
            : _ipcCallers.Select(caller => caller.Status).First(status => string.Equals(status.Name, ipcName, StringComparison.Ordinal));

    public IReadOnlyList<IpcStatus> GetUnavailableOptionalStatusesForChanges(CharacterDataChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        List<IpcStatus> statuses = [];
        AddUnavailableStatusForChange(statuses, changes, PlayerChanges.Customize, CustomizePlusIpcName);
        AddUnavailableStatusForChange(statuses, changes, PlayerChanges.Heels, HeelsIpcName);
        AddUnavailableStatusForChange(statuses, changes, PlayerChanges.Honorific, HonorificIpcName);
        AddUnavailableStatusForChange(statuses, changes, PlayerChanges.Moodles, MoodlesIpcName);
        AddUnavailableStatusForChange(statuses, changes, PlayerChanges.PetNames, PetNamesIpcName);
        return statuses;
    }

    private void PeriodicApiStateCheck()
    {
        foreach (var caller in _ipcCallers)
        {
            caller.CheckAPI();
        }

        Penumbra.CheckModDirectory();

        foreach (var caller in _ipcCallers)
        {
            ReportApiState(caller.Status);
        }
    }

    private void ReportApiState(IpcStatus status)
    {
        if (!_statusByName.TryGetValue(status.Name, out var previous))
        {
            _statusByName[status.Name] = status;
            return;
        }

        if (previous == status) return;

        _statusByName[status.Name] = status;
        Mediator.Publish(new IpcStatusChangedMessage(status));

        if (status.Role == IpcRole.Optional && previous.IsAvailable != status.IsAvailable)
        {
            Mediator.Publish(new OptionalIpcAvailabilityChangedMessage(status.Name, status.IsAvailable));
        }
    }

    private void AddUnavailableStatusForChange(List<IpcStatus> statuses, CharacterDataChangeSet changes, PlayerChanges change, string ipcName)
    {
        if (!changes.ContainsAny(change))
        {
            return;
        }

        var status = GetStatus(ipcName);
        if (!status.IsAvailable)
        {
            statuses.Add(status);
        }
    }
}
