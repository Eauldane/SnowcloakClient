namespace Snowcloak.Game.Objects;

public readonly record struct CharacterObjectState(
    nint Address,
    nint DrawObjectAddress,
    GameObjectDrawCondition DrawCondition,
    ushort? ObjectIndex,
    string Name,
    byte ClassJob,
    byte Gender,
    byte RaceId,
    byte TribeId,
    bool HasAppearanceData,
    bool HasHumanData,
    bool HasMainHandData,
    bool HasOffHandData);
