using Snowcloak.Services.ModNullification;

namespace Snowcloak.PlayerData.Moodles;

public static class ModNullificationMoodles
{
    private static readonly Guid HeightGuid = new("533bc706-a22b-47c2-a5cb-e12d577bc81d");
    private static readonly Guid VfxGuid = new("fda3aead-7cea-4459-950d-e4f41926bad8");
    private static readonly Guid SfxGuid = new("3033f02f-feb8-4cea-b455-0e697b846187");

    public static bool TryAppend(string? moodlesData, ModNullificationKind kinds, out string composedMoodlesData)
    {
        composedMoodlesData = moodlesData ?? string.Empty;
        if (kinds == ModNullificationKind.None)
        {
            return true;
        }

        if (!MoodlesDataParser.TryParsePayload(moodlesData, out var payload))
        {
            return false;
        }

        var statuses = payload.Statuses
            .Where(status => status.GUID != HeightGuid && status.GUID != VfxGuid && status.GUID != SfxGuid)
            .ToList();

        if (kinds.HasFlag(ModNullificationKind.Height))
        {
            statuses.Add(CreateStatus(HeightGuid, 215581, "Snowcloak: Height Nullified",
                "Snowcloak is nullifying this player's active height mod in line with your settings."));
        }

        if (kinds.HasFlag(ModNullificationKind.Vfx))
        {
            statuses.Add(CreateStatus(VfxGuid, 215012, "Snowcloak: VFX Nullified",
                "Snowcloak is nullifying this player's active VFX mod in line with your settings."));
        }

        if (kinds.HasFlag(ModNullificationKind.Sfx))
        {
            statuses.Add(CreateStatus(SfxGuid, 215005, "Snowcloak: SFX Nullified",
                "Snowcloak is nullifying this player's active SFX mod in line with your settings."));
        }

        payload.Statuses.Clear();
        payload.Statuses.AddRange(statuses);
        return MoodlesDataParser.TrySerializePayload(payload, out composedMoodlesData);
    }

    private static MoodlesStatusData CreateStatus(Guid guid, int icon, string title, string description)
    {
        return new MoodlesStatusData
        {
            GUID = guid,
            IconID = icon,
            Title = title,
            Description = description,
            Type = MoodlesStatusType.Special,
            Applier = "Snowcloak",
            ExpiresAt = long.MaxValue
        };
    }
}
