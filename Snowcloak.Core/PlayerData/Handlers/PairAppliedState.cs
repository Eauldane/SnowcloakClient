using CharacterData = Snowcloak.API.Data.CharacterData;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Handlers;

public sealed class PairAppliedState
{
    private readonly object _modRecoverySync = new();
    private bool _forceApplyMods;
    private int _modRecoveryGeneration;

    public CharacterData? CachedData { get; set; }
    public Dictionary<ObjectKind, Guid?> CustomizeIds { get; } = [];
    public nint LastKnownPlayerAddress { get; set; } = nint.Zero;
    public nint LastPlayerScopedOptionalAddress { get; set; } = nint.Zero;
    public bool ForceApplyMods
    {
        get
        {
            lock (_modRecoverySync)
            {
                return _forceApplyMods;
            }
        }
    }
    public bool RedrawOnNextApplication { get; set; }
    public bool HasPlayerScopedOptionalDataApplied { get; set; }

    public void RequireModRecovery()
    {
        lock (_modRecoverySync)
        {
            _forceApplyMods = true;
            _modRecoveryGeneration++;
        }
    }

    public int CaptureModRecoveryGeneration()
    {
        lock (_modRecoverySync)
        {
            return _forceApplyMods ? _modRecoveryGeneration : 0;
        }
    }

    public bool CompleteModRecovery(int generation)
    {
        if (generation == 0)
        {
            return false;
        }

        lock (_modRecoverySync)
        {
            if (_forceApplyMods && _modRecoveryGeneration == generation)
            {
                _forceApplyMods = false;
                return true;
            }

            return false;
        }
    }
}
