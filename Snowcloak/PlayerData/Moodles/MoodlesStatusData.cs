using MemoryPack;

namespace Snowcloak.PlayerData.Moodles;

[MemoryPackable]
public partial class MoodlesStatusData
{
    public Guid GUID;
    public int IconID;
    public string Title = string.Empty;
    public string Description = string.Empty;
    public string CustomFXPath = string.Empty;
    public long ExpiresAt;
    public MoodlesStatusType Type;
    public MoodlesModifiers Modifiers;
    public int Stacks = 1;
    public int StackSteps;
    public Guid ChainedStatus;
    public MoodlesChainTrigger ChainTrigger;
    public string Applier = string.Empty;
    public string Dispeller = string.Empty;
    public bool Persistent;
    public int Days;
    public int Hours;
    public int Minutes;
    public int Seconds;
    public bool NoExpire;
    public bool AsPermanent;
}

public enum MoodlesStatusType
{
    Positive,
    Negative,
    Special
}

[Flags]
public enum MoodlesModifiers : uint
{
    None = 0,
    CanDispel = 1u << 0,
    StacksIncrease = 1u << 1,
    StacksRollOver = 1u << 2,
    PersistExpireTime = 1u << 3,
    StacksMoveToChain = 1u << 4,
    StacksCarryToChain = 1u << 5,
    PersistAfterTrigger = 1u << 6,
}

public enum MoodlesChainTrigger
{
    Dispel = 0,
    HitMaxStacks = 1,
    TimerExpired = 2,
}

public static class MoodlesDataParser
{
    private static readonly MemoryPackSerializerOptions SerializerOptions = new()
    {
        StringEncoding = StringEncoding.Utf16,
    };

    public static bool TryParse(string? base64, out IReadOnlyList<MoodlesStatusData> statuses)
    {
        statuses = Array.Empty<MoodlesStatusData>();
        if (!TryParsePayload(base64, out var payload))
            return false;

        statuses = payload.Statuses;
        return true;
    }

    public static bool TryParsePayload(string? base64, out MoodlesStatusPayload payload)
    {
        payload = new MoodlesStatusPayload([], null);
        if (string.IsNullOrWhiteSpace(base64))
        {
            return true;
        }

        try
        {
            var data = Convert.FromBase64String(base64);
            var parsedStatuses = TryDeserializeStatuses(data, out var parsed);
            var parsedManager = TryDeserializeManager(data, out var manager);

            if (parsedStatuses && HasRenderableStatuses(parsed))
            {
                payload = new MoodlesStatusPayload(parsed, null);
                return true;
            }

            if (parsedManager && HasRenderableStatuses(manager.Statuses))
            {
                payload = new MoodlesStatusPayload(manager.Statuses, manager);
                return true;
            }

            if (parsedStatuses)
            {
                payload = new MoodlesStatusPayload(parsed, null);
                return true;
            }

            if (parsedManager)
            {
                payload = new MoodlesStatusPayload(manager.Statuses, manager);
                return true;
            }
        }
        catch
        {
            // handled by returning false below
        }

        return false;
    }

    public static bool TrySerializePayload(MoodlesStatusPayload payload, out string base64)
    {
        base64 = string.Empty;
        try
        {
            if (payload.Manager != null)
            {
                payload.Manager.Statuses = payload.Statuses;
                var managerData = MemoryPackSerializer.Serialize(payload.Manager, SerializerOptions);
                base64 = Convert.ToBase64String(managerData);
                return true;
            }

            return TrySerialize(payload.Statuses, out base64);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeManager(byte[] data, out MoodlesStatusManagerData manager)
    {
        manager = new MoodlesStatusManagerData();
        try
        {
            var parsed = MemoryPackSerializer.Deserialize<MoodlesStatusManagerData>(data, SerializerOptions);
            if (parsed == null)
                return false;

            manager = parsed;
            manager.Statuses ??= [];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeserializeStatuses(byte[] data, out List<MoodlesStatusData> statuses)
    {
        statuses = [];
        try
        {
            statuses = MemoryPackSerializer.Deserialize<List<MoodlesStatusData>>(data, SerializerOptions) ?? [];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasRenderableStatuses(IEnumerable<MoodlesStatusData>? statuses)
        => statuses?.Any(status => status.IconID > 0
            || !string.IsNullOrWhiteSpace(status.Title)
            || !string.IsNullOrWhiteSpace(status.Description)) == true;

    public static bool TrySerialize(IEnumerable<MoodlesStatusData> statuses, out string base64)
    {
        base64 = string.Empty;
        try
        {
            var data = MemoryPackSerializer.Serialize(statuses.ToList(), SerializerOptions);
            base64 = Convert.ToBase64String(data);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class MoodlesStatusPayload
{
    internal MoodlesStatusPayload(List<MoodlesStatusData> statuses, MoodlesStatusManagerData? manager)
    {
        Statuses = statuses;
        Manager = manager;
    }

    public List<MoodlesStatusData> Statuses { get; }
    internal MoodlesStatusManagerData? Manager { get; }
}

[MemoryPackable]
public partial class MoodlesStatusManagerData
{
    public List<Guid> AddTextShown = [];
    public List<Guid> RemTextShown = [];
    public List<MoodlesStatusData> Statuses = [];
    public bool Ephemeral;
    public bool WasTouchedByIPC;
}
