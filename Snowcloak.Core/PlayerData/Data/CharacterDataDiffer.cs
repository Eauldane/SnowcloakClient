using CharacterData = Snowcloak.API.Data.CharacterData;
using FileReplacementData = Snowcloak.API.Data.FileReplacementData;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Data;

public static class CharacterDataDiffer
{
    private static readonly ObjectKind[] ObjectKinds = Enum.GetValues<ObjectKind>();

    public static CharacterDataChangeSet Diff(CharacterData? oldData, CharacterData newData)
    {
        ArgumentNullException.ThrowIfNull(newData);

        oldData ??= new CharacterData();
        CharacterDataChangeSet changes = new();

        foreach (var objectKind in ObjectKinds)
        {
            CompareFilesAndGlamourer(oldData, newData, objectKind, changes);
            CompareCustomizePlus(oldData, newData, objectKind, changes);

            if (objectKind != ObjectKind.Player)
                continue;

            ComparePlayerData(oldData, newData, changes);
        }

        return changes;
    }

    private static void CompareFilesAndGlamourer(CharacterData oldData, CharacterData newData, ObjectKind objectKind, CharacterDataChangeSet changes)
    {
        var hasOldFiles = oldData.FileReplacements.TryGetValue(objectKind, out var oldFiles);
        var hasNewFiles = newData.FileReplacements.TryGetValue(objectKind, out var newFiles);
        var hasOldGlamourer = oldData.GlamourerData.TryGetValue(objectKind, out var oldGlamourer);
        var hasNewGlamourer = newData.GlamourerData.TryGetValue(objectKind, out var newGlamourer);

        if (hasNewFiles != hasOldFiles || hasNewGlamourer != hasOldGlamourer)
        {
            changes.Add(objectKind, PlayerChanges.ModFiles);
            changes.Add(objectKind, PlayerChanges.Glamourer);
            changes.Add(objectKind, PlayerChanges.ForcedRedraw);
            return;
        }

        if (hasOldFiles && hasNewFiles && oldFiles != null && newFiles != null && !FileReplacementsEqual(oldFiles, newFiles))
        {
            changes.Add(objectKind, PlayerChanges.ModFiles);
            changes.Add(objectKind, PlayerChanges.ForcedRedraw);
        }

        if (hasOldGlamourer && hasNewGlamourer && !string.Equals(oldGlamourer, newGlamourer, StringComparison.Ordinal))
        {
            changes.Add(objectKind, PlayerChanges.Glamourer);
        }
    }

    private static void CompareCustomizePlus(CharacterData oldData, CharacterData newData, ObjectKind objectKind, CharacterDataChangeSet changes)
    {
        oldData.CustomizePlusData.TryGetValue(objectKind, out var oldCustomizePlusData);
        newData.CustomizePlusData.TryGetValue(objectKind, out var newCustomizePlusData);

        oldCustomizePlusData ??= string.Empty;
        newCustomizePlusData ??= string.Empty;

        if (!string.Equals(oldCustomizePlusData, newCustomizePlusData, StringComparison.Ordinal))
        {
            changes.Add(objectKind, PlayerChanges.Customize);
        }
    }

    private static void ComparePlayerData(CharacterData oldData, CharacterData newData, CharacterDataChangeSet changes)
    {
        if (!string.Equals(oldData.ManipulationData, newData.ManipulationData, StringComparison.Ordinal))
        {
            changes.Add(ObjectKind.Player, PlayerChanges.ModManip);
            changes.Add(ObjectKind.Player, PlayerChanges.ForcedRedraw);
        }

        if (!string.Equals(oldData.HeelsData, newData.HeelsData, StringComparison.Ordinal))
        {
            changes.Add(ObjectKind.Player, PlayerChanges.Heels);
        }

        if (!string.Equals(oldData.HonorificData, newData.HonorificData, StringComparison.Ordinal))
        {
            changes.Add(ObjectKind.Player, PlayerChanges.Honorific);
        }

        if (!string.Equals(oldData.PetNamesData, newData.PetNamesData, StringComparison.Ordinal))
        {
            changes.Add(ObjectKind.Player, PlayerChanges.PetNames);
        }

        if (!string.Equals(oldData.MoodlesData, newData.MoodlesData, StringComparison.Ordinal))
        {
            changes.Add(ObjectKind.Player, PlayerChanges.Moodles);
        }
    }

    private static bool FileReplacementsEqual(List<FileReplacementData> oldFiles, List<FileReplacementData> newFiles)
    {
        if (oldFiles.Count != newFiles.Count)
            return false;

        for (var i = 0; i < oldFiles.Count; i++)
        {
            if (!FileReplacementEquals(oldFiles[i], newFiles[i]))
                return false;
        }

        return true;
    }

    private static bool FileReplacementEquals(FileReplacementData oldFile, FileReplacementData newFile)
        => string.Equals(oldFile.Hash, newFile.Hash, StringComparison.Ordinal)
           && string.Equals(oldFile.FileSwapPath, newFile.FileSwapPath, StringComparison.Ordinal)
           && oldFile.GamePaths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(newFile.GamePaths);
}
