namespace Snowcloak.PlayerData.Handlers;

public readonly record struct PairFilterContext(
    bool Paused,
    bool DisableAnimations,
    bool DisableSounds,
    bool DisableVFX,
    bool IsWhitelisted);
