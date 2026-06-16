using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public sealed class ProfileDetailsEditorSection
{
    public void Draw(ProfileEditSession session, Action markDirty)
    {
        CharacterProfileUiShared.DrawSectionTitle("RP Hooks");
        DrawHooksEditor(session, markDirty);

        CharacterProfileUiShared.DrawSectionTitle("Pair-only Details");
        ProfileEditorFieldControls.DrawMultiline(
            "Overview",
            session.Overview,
            ProfileEditSession.MaxLongTextLength,
            180f,
            value => session.Overview = value,
            markDirty);
        ProfileEditorFieldControls.DrawMultiline(
            "OOC notes",
            session.OocNotes,
            ProfileEditSession.MaxLongTextLength,
            100f,
            value => session.OocNotes = value,
            markDirty);
        if (session.ContentRating == ProfileContentRating.Adult)
        {
            ProfileEditorFieldControls.DrawMultiline(
                "Adult preferences",
                session.AdultPreferences,
                ProfileEditSession.MaxLongTextLength,
                100f,
                value => session.AdultPreferences = value,
                markDirty);
        }
    }

    private static void DrawHooksEditor(ProfileEditSession session, Action markDirty)
    {
        for (var i = 0; i < session.Hooks.Count; i++)
        {
            var hook = session.Hooks[i];
            using var id = ImRaii.PushId($"rp-hook-{i}");
            ImGui.BeginGroup();
            ImGui.TextColored(ImGuiColors.HealerGreen, $"Hook {i + 1}");
            ImGui.SameLine();
            ImGui.BeginDisabled(i == 0);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowUp, "Move hook up"))
            {
                session.MoveHook(i, -1);
                markDirty();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(i >= session.Hooks.Count - 1);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowDown, "Move hook down"))
            {
                session.MoveHook(i, 1);
                markDirty();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Remove hook"))
            {
                session.RemoveHookAt(i);
                markDirty();
                ImGui.EndGroup();
                continue;
            }

            ProfileEditorFieldControls.DrawShortInput("Title", hook.Title, value => hook.Title = value, markDirty);
            ProfileEditorFieldControls.DrawMultiline(
                "Description",
                hook.Description,
                ProfileEditSession.MaxLongTextLength,
                78f,
                value => hook.Description = value,
                markDirty);
            ImGui.EndGroup();
            ImGui.Separator();
        }

        ImGui.BeginDisabled(session.Hooks.Count >= ProfileEditSession.MaxHooks);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add hook"))
        {
            session.AddHook();
            markDirty();
        }
        ImGui.EndDisabled();
        if (session.Hooks.Count >= ProfileEditSession.MaxHooks)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Profiles can contain at most 32 hooks.");
        }
    }
}
