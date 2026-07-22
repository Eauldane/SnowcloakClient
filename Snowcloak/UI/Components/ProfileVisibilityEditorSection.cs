using Dalamud.Bindings.ImGui;
using Snowcloak.API.Data.Enum;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public static class ProfileVisibilityEditorSection
{
    public static void Draw(ProfileEditSession session, Action markDirty)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(markDirty);
        ImGui.TextWrapped("Choose who can see each part of this character profile.");
        if (!ImGui.BeginTable("profile-field-audiences", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Profile field", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Audience", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();
        var fields = session.FieldVisibility;
        DrawRow("Character name", fields.CharacterName, value => fields.CharacterName = value, markDirty);
        DrawRow("Title", fields.Title, value => fields.Title = value, markDirty);
        DrawRow("Pronouns", fields.Pronouns, value => fields.Pronouns = value, markDirty);
        DrawRow("Tagline", fields.Tagline, value => fields.Tagline = value, markDirty);
        DrawRow("RP status", fields.RpStatus, value => fields.RpStatus = value, markDirty);
        DrawRow("Approachability", fields.Approachability, value => fields.Approachability = value, markDirty);
        DrawRow("Header", fields.Header, value => fields.Header = value, markDirty);
        DrawRow("Portrait", fields.Portrait, value => fields.Portrait = value, markDirty);
        DrawRow("At a glance", fields.AtAGlance, value => fields.AtAGlance = value, markDirty);
        DrawRow("Overview", fields.Overview, value => fields.Overview = value, markDirty);
        DrawRow("Hooks", fields.Hooks, value => fields.Hooks = value, markDirty);
        DrawRow("OOC notes", fields.OocNotes, value => fields.OocNotes = value, markDirty);
        DrawRow("Adult preferences", fields.AdultPreferences, value => fields.AdultPreferences = value, markDirty);
        DrawRow("Boundaries", fields.Boundaries, value => fields.Boundaries = value, markDirty);
        DrawRow("Tags", fields.Tags, value => fields.Tags = value, markDirty);
        ImGui.EndTable();
    }

    private static void DrawRow(string label, ProfileAudience selected, Action<ProfileAudience> set, Action markDirty)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##audience-{label}", AudienceLabel(selected))) return;
        foreach (var audience in Enum.GetValues<ProfileAudience>())
        {
            if (ImGui.Selectable(AudienceLabel(audience), audience == selected))
            {
                set(audience);
                markDirty();
            }
        }
        ImGui.EndCombo();
    }

    private static string AudienceLabel(ProfileAudience audience) => audience switch
    {
        ProfileAudience.Public => "Public",
        ProfileAudience.Paired => "Paired users",
        ProfileAudience.Owner => "Only me",
        _ => "Only me",
    };
}
