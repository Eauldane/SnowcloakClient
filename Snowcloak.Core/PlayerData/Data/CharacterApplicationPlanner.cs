using CharacterData = Snowcloak.API.Data.CharacterData;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Data;

public static class CharacterApplicationPlanner
{
    public static void ApplyForceModifiers(CharacterDataChangeSet changes, CharacterData? oldData, CharacterData newData,
        bool forceApplyCustomization, bool forceApplyMods)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(newData);

        oldData ??= new CharacterData();

        if (forceApplyMods)
            ApplyForcedModChanges(changes, oldData, newData);

        if (forceApplyCustomization)
            ApplyForcedCustomizationChanges(changes, oldData, newData);
    }

    private static void ApplyForcedModChanges(CharacterDataChangeSet changes, CharacterData oldData, CharacterData newData)
    {
        foreach (var objectKind in Enum.GetValues<ObjectKind>())
        {
            if (oldData.FileReplacements.ContainsKey(objectKind) && newData.FileReplacements.ContainsKey(objectKind))
            {
                changes.Add(objectKind, PlayerChanges.ModFiles);
                changes.Add(objectKind, PlayerChanges.ForcedRedraw);
            }
        }

        changes.Add(ObjectKind.Player, PlayerChanges.ModManip);
        changes.Add(ObjectKind.Player, PlayerChanges.ForcedRedraw);
    }

    private static void ApplyForcedCustomizationChanges(CharacterDataChangeSet changes, CharacterData oldData, CharacterData newData)
    {
        foreach (var objectKind in Enum.GetValues<ObjectKind>())
        {
            if (oldData.GlamourerData.ContainsKey(objectKind) && newData.GlamourerData.ContainsKey(objectKind))
                changes.Add(objectKind, PlayerChanges.Glamourer);

            newData.CustomizePlusData.TryGetValue(objectKind, out var customizePlusData);
            if (!string.IsNullOrEmpty(customizePlusData))
                changes.Add(objectKind, PlayerChanges.Customize);
        }

        AddForcedPlayerCustomization(changes, newData);
    }

    private static void AddForcedPlayerCustomization(CharacterDataChangeSet changes, CharacterData newData)
    {
        if (!string.IsNullOrEmpty(newData.HeelsData))
            changes.Add(ObjectKind.Player, PlayerChanges.Heels);

        if (!string.IsNullOrEmpty(newData.HonorificData))
            changes.Add(ObjectKind.Player, PlayerChanges.Honorific);

        if (!string.IsNullOrEmpty(newData.PetNamesData))
            changes.Add(ObjectKind.Player, PlayerChanges.PetNames);

        if (!string.IsNullOrEmpty(newData.MoodlesData))
            changes.Add(ObjectKind.Player, PlayerChanges.Moodles);
    }
}
