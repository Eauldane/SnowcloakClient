using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public sealed class ProfileTagsEditorSection
{
    private string _newTagValue = string.Empty;
    private ProfileTagType _newTagType = ProfileTagType.ChatStyle;
    private string _tagEditorError = string.Empty;

    public void Draw(ProfileEditSession session, Action markDirty)
    {
        CharacterProfileUiShared.DrawSectionTitle("Tags");
        ProfileEditorFieldControls.DrawFieldLabel("Tag type");
        if (ImGui.BeginCombo("##rp-profile-tag-type", ProfileTagPolicy.GetTypeLabel(_newTagType)))
        {
            foreach (var tagType in Enum.GetValues<ProfileTagType>())
            {
                if (ImGui.Selectable(ProfileTagPolicy.GetTypeLabel(tagType), tagType == _newTagType))
                {
                    _newTagType = tagType;
                }
            }

            ImGui.EndCombo();
        }

        ProfileEditorFieldControls.DrawFieldLabel("New tag");
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputTextWithHint(
                "##rp-profile-tag-input",
                "Type a tag value...",
                ref _newTagValue,
                ProfileTagPolicy.MaxTagLength,
                ImGuiInputTextFlags.EnterReturnsTrue))
        {
            AddTag(session, markDirty);
        }

        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add tag"))
        {
            AddTag(session, markDirty);
        }

        var suggestions = ProfileTagPolicy.GetDefaultSuggestions(_newTagType, _newTagValue, session.Tags, 8);
        if (suggestions.Count > 0)
        {
            ProfileEditorFieldControls.DrawFieldLabel("Suggested defaults");
            if (ImGui.BeginCombo("##rp-profile-tag-suggestions", "Choose a suggestion..."))
            {
                foreach (var suggestion in suggestions)
                {
                    if (!ImGui.Selectable(suggestion))
                    {
                        continue;
                    }

                    _newTagValue = suggestion;
                    AddTag(session, markDirty);
                }

                ImGui.EndCombo();
            }
        }

        if (!string.IsNullOrWhiteSpace(_tagEditorError))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, _tagEditorError);
        }

        var removed = ProfileTagChipRenderer.DrawTagChips(session.Tags, "rp-profile-editor-tags");
        if (removed >= 0)
        {
            session.RemoveTagAt(removed);
            markDirty();
        }
    }

    private void AddTag(ProfileEditSession session, Action markDirty)
    {
        var value = _newTagValue.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            _tagEditorError = "Tag value cannot be empty.";
            return;
        }

        if (!session.TryAddTag(_newTagType, value))
        {
            _tagEditorError = "That tag already exists or the profile has reached its tag limit.";
            return;
        }

        _newTagValue = string.Empty;
        _tagEditorError = string.Empty;
        markDirty();
    }
}
