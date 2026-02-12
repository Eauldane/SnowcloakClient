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
        if (string.IsNullOrWhiteSpace(base64))
        {
            return true;
        }

        try
        {
            var data = Convert.FromBase64String(base64);
            var parsed = MemoryPackSerializer.Deserialize<List<MoodlesStatusData>>(data, SerializerOptions);
            statuses = parsed != null ? parsed : Array.Empty<MoodlesStatusData>();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
