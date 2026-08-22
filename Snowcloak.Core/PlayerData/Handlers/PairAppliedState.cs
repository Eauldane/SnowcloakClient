using CharacterData = Snowcloak.API.Data.CharacterData;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Handlers;

public sealed class PairAppliedState
{
    public CharacterData? CachedData { get; set; }
    public Dictionary<ObjectKind, Guid?> CustomizeIds { get; } = [];
    public nint LastKnownPlayerAddress { get; set; } = nint.Zero;
    public nint LastPlayerScopedOptionalAddress { get; set; } = nint.Zero;
    public bool ForceApplyMods { get; set; }
    public bool RedrawOnNextApplication { get; set; }
    public bool HasPlayerScopedOptionalDataApplied { get; set; }
}
