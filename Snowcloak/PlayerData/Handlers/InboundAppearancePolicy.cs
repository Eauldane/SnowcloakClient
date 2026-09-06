using System.Security.Cryptography;
using System.Text;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.TemporaryAppearance;

namespace Snowcloak.PlayerData.Handlers;

public static class InboundAppearancePolicy
{
    public const ushort PolicyVersion = 1;

    public static DerivedAppearance Derive(CharacterData canonical, PairFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        var data = canonical.Clone();
        var allowed = context.AllowedCategories;

        if ((allowed & AppearanceCategoryMask.PlayerVisual) == 0)
        {
            data.FileReplacements.Remove(ObjectKind.Player);
            data.GlamourerData.Remove(ObjectKind.Player);
            data.CustomizePlusData.Remove(ObjectKind.Player);
            data.ManipulationData = string.Empty;
            data.HeelsData = string.Empty;
        }
        if ((allowed & AppearanceCategoryMask.BodyScale) == 0)
            data.CustomizePlusData.Remove(ObjectKind.Player);
        if ((allowed & (AppearanceCategoryMask.Mount | AppearanceCategoryMask.Minion))
            != (AppearanceCategoryMask.Mount | AppearanceCategoryMask.Minion))
            RemoveKind(data, ObjectKind.MinionOrMount);
        RemoveKind(data, ObjectKind.FashionAccessory);
        if ((allowed & AppearanceCategoryMask.Pet) == 0)
            RemoveKind(data, ObjectKind.Pet);
        if ((allowed & AppearanceCategoryMask.Companion) == 0)
            RemoveKind(data, ObjectKind.Companion);
        if ((allowed & AppearanceCategoryMask.AppearanceExtras) == 0)
        {
            data.HonorificData = string.Empty;
            data.MoodlesData = string.Empty;
            data.PetNamesData = string.Empty;
            data.ExtensionData.Clear();
        }

        foreach (var objectKind in data.FileReplacements.Keys.ToList())
        {
            data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                .Select(file => new FileReplacementData
                {
                    Hash = file.Hash,
                    FileSwapPath = file.FileSwapPath,
                    GamePaths = file.GamePaths.Where(path =>
                        ((allowed & AppearanceCategoryMask.Sound) != 0 || !IsSound(path))
                        && ((allowed & AppearanceCategoryMask.Animation) != 0 || !IsAnimation(path))
                        && ((allowed & AppearanceCategoryMask.Vfx) != 0 || !IsVfx(path))).ToArray(),
                })
                .Where(file => file.GamePaths.Length > 0)
                .ToList();
        }

        var identityInput = Encoding.UTF8.GetBytes($"{canonical.DataHash.Value}:{PolicyVersion}:{(ulong)allowed}");
        return new DerivedAppearance(data, Convert.ToHexString(SHA256.HashData(identityInput)), allowed, PolicyVersion);
    }

    private static void RemoveKind(CharacterData data, ObjectKind kind)
    {
        data.FileReplacements.Remove(kind);
        data.GlamourerData.Remove(kind);
        data.CustomizePlusData.Remove(kind);
    }

    private static bool IsSound(string path) => path.EndsWith("scd", StringComparison.OrdinalIgnoreCase);
    private static bool IsAnimation(string path) => path.EndsWith("tmb", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("pap", StringComparison.OrdinalIgnoreCase);
    private static bool IsVfx(string path) => path.EndsWith("atex", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("avfx", StringComparison.OrdinalIgnoreCase);
}

public sealed record DerivedAppearance(CharacterData Data, string EffectiveIdentity,
    AppearanceCategoryMask AllowedCategories, ushort PolicyVersion);
