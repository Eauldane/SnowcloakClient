using CharacterData = Snowcloak.API.Data.CharacterData;
using FileReplacementData = Snowcloak.API.Data.FileReplacementData;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Data;

public static class CharacterDataCloneExtensions
{
    public static CharacterData Clone(this CharacterData source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new CharacterData
        {
            CustomizePlusData = CloneDictionary(source.CustomizePlusData),
            FileReplacements = source.FileReplacements.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Select(Clone).ToList()),
            GlamourerData = CloneDictionary(source.GlamourerData),
            HeelsData = source.HeelsData,
            HonorificData = source.HonorificData,
            ManipulationData = source.ManipulationData,
            MoodlesData = source.MoodlesData,
            PetNamesData = source.PetNamesData,
        };
    }

    public static FileReplacementData Clone(this FileReplacementData source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new FileReplacementData
        {
            FileSwapPath = source.FileSwapPath,
            GamePaths = [.. source.GamePaths],
            Hash = source.Hash,
        };
    }

    private static Dictionary<ObjectKind, string> CloneDictionary(Dictionary<ObjectKind, string> source)
        => source.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}
