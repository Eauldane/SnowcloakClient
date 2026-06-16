using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public sealed class ProfilePublishEditorSection
{
    public void DrawChecklist(ProfileEditSession session)
    {
        var document = session.ToDocument();
        var checks = GetCompletenessChecks(document);
        var complete = checks.Count(check => check.Complete);
        var completion = checks.Length == 0 ? 0f : complete / (float)checks.Length;

        CharacterProfileUiShared.DrawSectionTitle("Profile Checklist");
        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, SnowcloakColours.OnlineBlue))
        {
            ImGui.ProgressBar(completion, new System.Numerics.Vector2(-1f, 0f), $"{complete}/{checks.Length} prompts");
        }

        ImGui.TextColored(
            complete >= checks.Length - 1 ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow,
            complete >= checks.Length - 1
                ? "This profile is ready to publish."
                : "These hints are only shown here while editing.");

        foreach (var check in checks)
        {
            ImGui.TextColored(check.Complete ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey, check.Complete ? "[x]" : "[ ]");
            ImGui.SameLine();
            ImGui.TextUnformatted(check.Label);
            if (!check.Complete)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"- {check.Hint}");
            }
        }
    }

    public void DrawContentControls(ProfileEditSession session, Action markDirty)
    {
        CharacterProfileUiShared.DrawSectionTitle("Publishing");
        DrawRatingCombo("Public profile rating", session.PublicContentRating, value => session.PublicContentRating = value, markDirty);
        DrawRatingCombo("Full profile rating", session.ContentRating, value => session.ContentRating = value, markDirty);
        if ((int)session.ContentRating < (int)session.PublicContentRating)
        {
            session.ContentRating = session.PublicContentRating;
            markDirty();
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "Adult public views are hidden locally for viewers who disabled adult profile content.");
    }

    private static void DrawRatingCombo(string label, ProfileContentRating value, Action<ProfileContentRating> setValue, Action markDirty)
    {
        ProfileEditorFieldControls.DrawFieldLabel(label);
        if (!ImGui.BeginCombo($"##{label}", value.ToString()))
        {
            return;
        }

        foreach (var rating in Enum.GetValues<ProfileContentRating>())
        {
            if (ImGui.Selectable(rating.ToString(), rating == value))
            {
                setValue(rating);
                markDirty();
            }
        }

        ImGui.EndCombo();
    }

    private static ProfileChecklistItem[] GetCompletenessChecks(CharacterProfileDocumentDto document)
        =>
        [
            new("Character name", !string.IsNullOrWhiteSpace(document.CharacterName), "Give viewers a name to remember."),
            new("Pronouns", !string.IsNullOrWhiteSpace(document.Pronouns), "Helps nearby players address you cleanly."),
            new("Tagline or at-a-glance detail", !string.IsNullOrWhiteSpace(document.Tagline) || document.AtAGlance.Count > 0, "Add a fast hook for Frostbrand cards."),
            new("RP status or approachability", !string.IsNullOrWhiteSpace(document.RpStatus) || !string.IsNullOrWhiteSpace(document.Approachability), "Use this for IC/OOC and approach badges."),
            new("At least one RP hook", document.Hooks.Count > 0, "Give other roleplayers something specific to respond to."),
            new("At least one tag", document.Tags.Count > 0, "Tags help compatible players find you."),
            new("Portrait", !string.IsNullOrWhiteSpace(document.ProfilePictureHash), "A portrait makes public cards easier to scan."),
        ];

    private readonly record struct ProfileChecklistItem(string Label, bool Complete, string Hint);
}
