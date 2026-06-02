using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Moodles;

namespace Snowcloak.Services.ModNullification;

public sealed class ModNullificationService
{
    private readonly HumanCmpDefaultsProvider _humanCmpDefaultsProvider;
    private readonly ILogger<ModNullificationService> _logger;
    private readonly PlayerPerformanceConfigService _performanceConfigService;

    public ModNullificationService(ILogger<ModNullificationService> logger,
        PlayerPerformanceConfigService performanceConfigService,
        HumanCmpDefaultsProvider humanCmpDefaultsProvider)
    {
        _logger = logger;
        _performanceConfigService = performanceConfigService;
        _humanCmpDefaultsProvider = humanCmpDefaultsProvider;
    }

    public ModNullificationKind Apply(CharacterData data, bool isWhitelisted)
    {
        var config = _performanceConfigService.Current;
        if (isWhitelisted)
        {
            return ModNullificationKind.None;
        }

        var kinds = ModNullificationKind.None;

        var removedVfxCount = config.NullifyVfx ? RemoveFileReplacements(data, ".atex", ".avfx") : 0;
        if (removedVfxCount > 0)
        {
            kinds |= ModNullificationKind.Vfx;
            _logger.LogDebug("Nullified {count} custom VFX replacements", removedVfxCount);
        }

        var removedSfxCount = config.NullifySfx ? RemoveFileReplacements(data, ".scd") : 0;
        if (removedSfxCount > 0)
        {
            kinds |= ModNullificationKind.Sfx;
            _logger.LogDebug("Nullified {count} custom SFX replacements", removedSfxCount);
        }

        var nullifyAllHeightMods = config.NullifyAllHeightMods;
        var evaluation = default(HeightEvaluation);
        if (nullifyAllHeightMods || ShouldNullifyHeight(data, out evaluation))
        {
            if (PenumbraMetaManipulationCodec.TryRemoveHeightEntries(data.ManipulationData, out var filteredManipulations, out var removedCount))
            {
                if (removedCount > 0)
                {
                    data.ManipulationData = filteredManipulations;
                    kinds |= ModNullificationKind.Height;
                    if (nullifyAllHeightMods)
                    {
                        _logger.LogDebug("Nullified {count} height RSP entries because all height mods are disabled", removedCount);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Nullified {count} height RSP entries: clan={clan}, female={female}, slider={slider}, scale={scale}, estimatedCm={estimatedCm}",
                            removedCount, evaluation.Clan, evaluation.Female, evaluation.HeightSlider, evaluation.EffectiveScale, evaluation.EstimatedCentimeters);
                    }
                }
            }
            else
            {
                _logger.LogWarning("Unable to remove height RSP entries from the received Penumbra manipulation payload");
            }
        }

        if (config.ShowModNullificationMoodles
            && kinds != ModNullificationKind.None
            && !TryAppendMoodles(data, kinds))
        {
            _logger.LogWarning("Unable to append local mod nullification Moodles markers");
        }

        return kinds;
    }

    private static bool TryAppendMoodles(CharacterData data, ModNullificationKind kinds)
    {
        if (!ModNullificationMoodles.TryAppend(data.MoodlesData, kinds, out var composedMoodlesData))
        {
            return false;
        }

        data.MoodlesData = composedMoodlesData;
        return true;
    }

    private bool ShouldNullifyHeight(CharacterData data, out HeightEvaluation evaluation)
    {
        evaluation = default;
        var config = _performanceConfigService.Current;
        if (!config.NullifyHeightAboveNormalMaxPercent
            && !config.NullifyHeightAboveEstimatedCentimeters)
        {
            return false;
        }

        if (!data.GlamourerData.TryGetValue(ObjectKind.Player, out var glamourerData)
            || !GlamourerAppearanceReader.TryRead(glamourerData, out var appearance)
            || !appearance.Clan.HasValue
            || !appearance.Gender.HasValue
            || !appearance.Height.HasValue)
        {
            return false;
        }

        if (!PenumbraMetaManipulationCodec.TryReadHeightEntries(data.ManipulationData, out var rspEntries))
        {
            _logger.LogWarning("Unable to inspect the received Penumbra manipulation payload for height RSP entries");
            return false;
        }

        var clan = appearance.Clan.Value;
        var female = appearance.Gender.Value != 0;
        var minimumAttribute = female ? RspAttribute.FemaleMinSize : RspAttribute.MaleMinSize;
        var maximumAttribute = female ? RspAttribute.FemaleMaxSize : RspAttribute.MaleMaxSize;
        var minimumIdentifier = new RspIdentifier(clan, minimumAttribute);
        var maximumIdentifier = new RspIdentifier(clan, maximumAttribute);

        var hasMinimumOverride = rspEntries.TryGetValue(minimumIdentifier, out var minimum);
        var hasMaximumOverride = rspEntries.TryGetValue(maximumIdentifier, out var maximum);
        if (!hasMinimumOverride && !hasMaximumOverride)
        {
            return false;
        }

        if (!_humanCmpDefaultsProvider.TryGetHeightScales(clan, female, out var defaultMinimum, out var defaultMaximum))
        {
            _logger.LogWarning("Unable to read vanilla height defaults for clan {clan}, female={female}", clan, female);
            return false;
        }

        minimum = hasMinimumOverride ? minimum : defaultMinimum;
        maximum = hasMaximumOverride ? maximum : defaultMaximum;
        var effectiveScale = (maximum - minimum) * appearance.Height.Value / 100f + minimum;

        HeightReference.TryConvertToCentimeters(clan, female, effectiveScale, out var estimatedCentimeters);
        evaluation = new HeightEvaluation(clan, female, appearance.Height.Value, effectiveScale, estimatedCentimeters);

        return config.NullifyHeightAboveNormalMaxPercent
                && effectiveScale > defaultMaximum * config.HeightNormalMaxPercent / 100f
            || config.NullifyHeightAboveEstimatedCentimeters
                && estimatedCentimeters > config.HeightEstimatedCentimeters;
    }

    private static int RemoveFileReplacements(CharacterData data, params string[] extensions)
    {
        var removedCount = 0;
        foreach (var objectKind in data.FileReplacements.Keys.ToList())
        {
            var filteredReplacements = data.FileReplacements[objectKind]
                .Where(replacement => !replacement.GamePaths.Any(path =>
                    extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))))
                .ToList();
            removedCount += data.FileReplacements[objectKind].Count - filteredReplacements.Count;
            data.FileReplacements[objectKind] = filteredReplacements;
        }

        return removedCount;
    }

    private readonly record struct HeightEvaluation(byte Clan, bool Female, byte HeightSlider, float EffectiveScale, float EstimatedCentimeters);
}
