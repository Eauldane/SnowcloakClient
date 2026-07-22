using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Roleplay;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public static class ProfileBoundariesEditorSection
{
    private static readonly (string Key, string Label)[] Vocabulary =
    [
        ("romance", "Romance"),
        ("sexual-themes", "Sexual themes"),
        ("violence", "Violence"),
        ("injury-gore", "Injury or gore"),
        ("horror", "Horror"),
        ("death", "Death"),
        ("captivity-restraint", "Captivity or restraint"),
        ("power-imbalance", "Power imbalance"),
        ("substance-use", "Substance use"),
        ("discrimination", "Discrimination"),
        ("pregnancy-family", "Pregnancy or family"),
        ("lore-divergence", "Lore divergence"),
    ];

    public static void Draw(ProfileEditSession session, Action markDirty)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(markDirty);
        var boundaries = session.Boundaries ?? new RpBoundariesDto();
        ImGui.TextWrapped("Set optional scene boundaries. These are profile guidance, not a substitute for talking to the other player.");
        ImGuiHelpers.ScaledDummy(5f);

        if (ImGui.BeginTable("rp-boundaries", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Boundary", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("Preference", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();
            foreach (var item in Vocabulary)
                DrawBoundaryRow(session, boundaries, item.Key, item.Label, markDirty);
            ImGui.EndTable();
        }

        ProfileEditorFieldControls.DrawMultiline("Additional note", boundaries.Note, 2000, 100f,
            value =>
            {
                boundaries.Note = value;
                session.Boundaries = boundaries;
            }, markDirty);
        var acknowledge = boundaries.RequireAcknowledgement;
        if (ImGui.Checkbox("Require viewers to acknowledge before expanding", ref acknowledge))
        {
            boundaries.RequireAcknowledgement = acknowledge;
            session.Boundaries = boundaries;
            markDirty();
        }
    }

    private static void DrawBoundaryRow(ProfileEditSession session, RpBoundariesDto boundaries, string key, string displayLabel, Action markDirty)
    {
        var entry = boundaries.Entries.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(displayLabel);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
        var label = entry == null ? "Not specified" : RatingLabel(entry.Rating);
        if (!ImGui.BeginCombo("##boundary-" + key, label))
            return;

        if (ImGui.Selectable("Not specified", entry == null) && entry != null)
        {
            boundaries.Entries.Remove(entry);
            session.Boundaries = boundaries;
            markDirty();
        }

        foreach (var rating in Enum.GetValues<RpBoundaryRating>())
        {
            if (!ImGui.Selectable(RatingLabel(rating), entry?.Rating == rating))
                continue;
            if (entry == null)
                boundaries.Entries.Add(new RpBoundaryEntryDto { Key = key, Rating = rating });
            else
                entry.Rating = rating;
            session.Boundaries = boundaries;
            markDirty();
        }
        ImGui.EndCombo();
    }

    private static string RatingLabel(RpBoundaryRating rating) => rating switch
    {
        RpBoundaryRating.Willing => "Willing",
        RpBoundaryRating.AskFirst => "Ask first",
        RpBoundaryRating.HardNo => "Hard no",
        _ => rating.ToString(),
    };
}
