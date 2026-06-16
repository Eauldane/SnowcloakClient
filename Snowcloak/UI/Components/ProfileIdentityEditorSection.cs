using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public sealed class ProfileIdentityEditorSection
{
    public void Draw(ProfileEditSession session, Action markDirty)
    {
        CharacterProfileUiShared.DrawSectionTitle("Public Character Card");
        ProfileEditorFieldControls.DrawShortInput("Character name", session.CharacterName, value => session.CharacterName = value, markDirty);
        ProfileEditorFieldControls.DrawShortInput("Title or epithet", session.Title, value => session.Title = value, markDirty);
        ProfileEditorFieldControls.DrawShortInput("Pronouns", session.Pronouns, value => session.Pronouns = value, markDirty);
        ProfileEditorFieldControls.DrawShortInput("Tagline", session.Tagline, value => session.Tagline = value, markDirty);
        ProfileEditorFieldControls.DrawShortInput("RP status", session.RpStatus, value => session.RpStatus = value, markDirty);
        ProfileEditorFieldControls.DrawShortInput("Approachability", session.Approachability, value => session.Approachability = value, markDirty);
        ProfileEditorFieldControls.DrawMultiline(
            "At a glance",
            session.AtAGlanceText,
            ProfileEditSession.MaxAtAGlanceTextLength,
            86f,
            value => session.AtAGlanceText = value,
            markDirty);
    }
}
