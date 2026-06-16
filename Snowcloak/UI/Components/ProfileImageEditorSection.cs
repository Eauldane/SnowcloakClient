using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using ElezenTools.UI;
using Snowcloak.Core.Profiles;
using System.Numerics;

namespace Snowcloak.UI.Components;

public sealed class ProfileImageEditorSection
{
    private static readonly ThemePreset[] ThemePresets =
    [
        new("Frost", "#2E94D1"),
        new("Aurora", "#47B878"),
        new("Amethyst", "#9B6BD3"),
        new("Ember", "#D45D3D"),
        new("Rose", "#D66A9A"),
        new("Gold", "#D6A43B"),
    ];

    public void Draw(
        ProfileEditSession session,
        IDalamudTextureWrap? headerImageTexture,
        IDalamudTextureWrap? profileImageTexture,
        Action chooseHeaderImage,
        Action removeHeaderImage,
        Action choosePortrait,
        Action removePortrait,
        Action markDirty)
    {
        CharacterProfileUiShared.DrawSectionTitle("Profile Appearance");
        DrawAccentControls(session, markDirty);
        DrawHeaderControls(session, headerImageTexture, chooseHeaderImage, removeHeaderImage);
        DrawPortraitControls(session, profileImageTexture, choosePortrait, removePortrait);
    }

    private static void DrawAccentControls(ProfileEditSession session, Action markDirty)
    {
        ProfileEditorFieldControls.DrawFieldLabel("Header accent colour");
        var headerAccentColor = ToVector3(CharacterProfileUiShared.ParseAccentColor(session.HeaderAccentColorHex));
        if (ImGui.ColorEdit3(
                "##rp-profile-header-accent",
                ref headerAccentColor,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.Uint8))
        {
            session.HeaderAccentColorHex = CharacterProfileUiShared.ToAccentColorHex(headerAccentColor);
            markDirty();
        }

        ProfileEditorFieldControls.DrawFieldLabel("Theme presets");
        for (var i = 0; i < ThemePresets.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            var preset = ThemePresets[i];
            if (ImGui.Button(preset.Name))
            {
                session.HeaderAccentColorHex = preset.AccentHex;
                markDirty();
            }
        }
    }

    private static void DrawHeaderControls(
        ProfileEditSession session,
        IDalamudTextureWrap? headerImageTexture,
        Action chooseHeaderImage,
        Action removeHeaderImage)
    {
        ProfileEditorFieldControls.DrawFieldLabel("Header image");
        if (headerImageTexture != null)
        {
            var maxWidth = ImGui.GetContentRegionAvail().X;
            var maxHeight = 96f * ImGuiHelpers.GlobalScale;
            var scale = MathF.Min(maxWidth / headerImageTexture.Width, maxHeight / headerImageTexture.Height);
            scale = MathF.Min(scale, 1f);
            ImGui.Image(headerImageTexture.Handle, new Vector2(headerImageTexture.Width * scale, headerImageTexture.Height * scale));
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileUpload, "Choose header image"))
        {
            chooseHeaderImage();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(session.HeaderImageHash));
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Remove header image"))
        {
            removeHeaderImage();
        }
        ImGui.EndDisabled();
    }

    private static void DrawPortraitControls(
        ProfileEditSession session,
        IDalamudTextureWrap? profileImageTexture,
        Action choosePortrait,
        Action removePortrait)
    {
        CharacterProfileUiShared.DrawSectionTitle("Public Portrait");
        if (profileImageTexture != null)
        {
            var max = 128f * ImGuiHelpers.GlobalScale;
            var scale = max / MathF.Max(profileImageTexture.Width, profileImageTexture.Height);
            ImGui.Image(profileImageTexture.Handle, new Vector2(profileImageTexture.Width * scale, profileImageTexture.Height * scale));
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileUpload, "Choose portrait"))
        {
            choosePortrait();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(session.ProfileImageHash));
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Remove portrait"))
        {
            removePortrait();
        }
        ImGui.EndDisabled();
    }

    private static Vector3 ToVector3(Vector4 color) => new(color.X, color.Y, color.Z);

    private readonly record struct ThemePreset(string Name, string AccentHex);
}
