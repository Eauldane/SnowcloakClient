using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace Snowcloak.Services.ModNullification;

public sealed class HumanCmpDefaultsProvider
{
    private const string HumanCmpPath = "chara/xls/charamake/human.cmp";
    private const int ClanCount = 16;
    private const int ScaleGroupCount = 8;
    private const int BodyTypeScaleCount = 10;
    private const int ScaleSize = 56;
    private const int RacialScalingSize = ScaleGroupCount * BodyTypeScaleCount * ScaleSize;
    private const int MaleMinimumOffset = 0;
    private const int MaleMaximumOffset = 4;
    private const int FemaleMinimumOffset = 16;
    private const int FemaleMaximumOffset = 20;

    private readonly Lazy<byte[]?> _humanCmpData;

    public HumanCmpDefaultsProvider(IDataManager dataManager, ILogger<HumanCmpDefaultsProvider> logger)
    {
        _humanCmpData = new Lazy<byte[]?>(() =>
        {
            try
            {
                var data = dataManager.GetFile(HumanCmpPath)?.Data;
                if (data == null || data.Length < RacialScalingSize)
                {
                    logger.LogWarning("Unable to load vanilla height defaults from {path}", HumanCmpPath);
                    return null;
                }

                return data;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to load vanilla height defaults from {path}", HumanCmpPath);
                return null;
            }
        });
    }

    public bool TryGetHeightScales(byte clan, bool female, out float minimum, out float maximum)
    {
        minimum = 0f;
        maximum = 0f;

        var data = _humanCmpData.Value;
        if (data == null || clan is < 1 or > ClanCount)
        {
            return false;
        }

        var clanIndex = clan - 1;
        var scaleIndex = (clanIndex >> 1) * BodyTypeScaleCount + (clanIndex & 1);
        var scaleOffset = data.Length - RacialScalingSize + scaleIndex * ScaleSize;
        var minimumOffset = scaleOffset + (female ? FemaleMinimumOffset : MaleMinimumOffset);
        var maximumOffset = scaleOffset + (female ? FemaleMaximumOffset : MaleMaximumOffset);

        if (maximumOffset + sizeof(float) > data.Length)
        {
            return false;
        }

        minimum = BitConverter.ToSingle(data, minimumOffset);
        maximum = BitConverter.ToSingle(data, maximumOffset);
        return float.IsFinite(minimum)
            && float.IsFinite(maximum)
            && minimum > 0f
            && maximum >= minimum;
    }
}
