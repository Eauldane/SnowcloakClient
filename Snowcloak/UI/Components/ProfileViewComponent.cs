using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Dto.Roleplay;
using Snowcloak.Configuration;
using Snowcloak.Services;
using System.Numerics;

namespace Snowcloak.UI.Components;

public sealed class ProfileViewComponent
{
    private readonly BbCodeRenderService _bbCodeRenderService;
    private readonly TextureService _textureService;
    private readonly UiFontService _fontService;
    private readonly SnowcloakConfigService _configService;
    private readonly HashSet<string> _expandedGeneralBoundaries = new(StringComparer.Ordinal);

    public ProfileViewComponent(
        UiFontService fontService,
        BbCodeRenderService bbCodeRenderService,
        TextureService textureService,
        SnowcloakConfigService configService)
    {
        _fontService = fontService;
        _bbCodeRenderService = bbCodeRenderService;
        _textureService = textureService;
        _configService = configService;
    }

    public void DrawStandalone(ProfileViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CharacterProfileUiShared.DrawHeader(request.Profile.Document, request.FallbackName, headerImageTexture: request.HeaderImageTexture);
        ImGui.Spacing();
        CharacterProfileUiShared.DrawProfileBadges(request.Profile.Document, $"{request.IdPrefix}-badges");

        request.DrawReportButton?.Invoke();
        if (request.DrawReportButton != null)
        {
            ImGui.SameLine();
        }

        var updated = request.Profile.UpdatedAtUtc.HasValue ? $"  |  updated {request.Profile.UpdatedAtUtc.Value:u}" : string.Empty;
        ImGui.TextColored(
            ImGuiColors.DalamudGrey,
            $"{request.Profile.Visibility} profile  |  revision {request.Profile.Revision}  |  {request.Profile.Document.ContentRating}{updated}");

        CharacterProfileUiShared.DrawMoodles(request.MoodlesData, request.IdPrefix, _textureService);
        if (DrawUnavailableProfileMessage(request.Profile))
        {
            return;
        }

        DrawFullProfileBody(request.Profile, request.ProfileImageTexture, request.VisibleTags, request.IdPrefix);
        request.DrawPairingDetails?.Invoke();
    }

    public void DrawCompact(ProfileViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CharacterProfileUiShared.DrawHeader(
            request.Profile.Document,
            request.FallbackName,
            compact: true,
            headerImageTexture: request.HeaderImageTexture);
        CharacterProfileUiShared.DrawProfileBadges(request.Profile.Document, $"{request.IdPrefix}-badges");

        if (!string.IsNullOrWhiteSpace(request.MoodlesData))
        {
            CharacterProfileUiShared.DrawMoodles(request.MoodlesData, request.IdPrefix, _textureService, maxVisible: 6);
        }

        if (DrawUnavailableProfileMessage(request.Profile))
        {
            return;
        }

        DrawPortrait(request.ProfileImageTexture, 240f * ImGuiHelpers.GlobalScale, showEmptyLabel: false);
        if (!string.IsNullOrWhiteSpace(request.Profile.Document.Tagline))
        {
            ImGui.TextWrapped(request.Profile.Document.Tagline);
        }

        CharacterProfileUiShared.DrawLabelValue("Approach:", request.Profile.Document.Approachability);
        foreach (var glance in request.Profile.Document.AtAGlance.Take(3))
        {
            DrawWrappedBullet(glance);
        }

        DrawTags(request.VisibleTags, $"{request.IdPrefix}-tags");
        DrawBbCodeSection("Overview", request.Profile.Description);
    }

    public void DrawEditorPreview(
        CharacterProfileDocumentDto document,
        string fallbackName,
        IDalamudTextureWrap? headerImageTexture,
        IReadOnlyList<UserProfileTagDto> tags,
        string idPrefix,
        bool fullProfile)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(tags);
        CharacterProfileUiShared.DrawHeader(document, fallbackName, headerImageTexture: headerImageTexture);
        CharacterProfileUiShared.DrawProfileBadges(document, $"{idPrefix}-badges");
        ImGui.Spacing();

        using (var table = ImRaii.Table($"{idPrefix}-summary", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 110f * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
                DrawPreviewLabelValue("Pronouns:", document.Pronouns);
                DrawPreviewLabelValue("RP status:", document.RpStatus);
                DrawPreviewLabelValue("Approach:", document.Approachability);
            }
        }

        DrawAtAGlance(document.AtAGlance);
        DrawPlainHooks(document.Hooks);
        if (fullProfile)
        {
            DrawPlainTextSection("Overview", document.Overview);
            DrawPlainTextSection("OOC Notes", document.OocNotes);
            if (document.ContentRating == ProfileContentRating.Adult)
            {
                DrawPlainTextSection("Adult Preferences", document.AdultPreferences);
            }
            DrawBoundaries(document.Boundaries, null, 0, true,
                document.ContentRating == ProfileContentRating.Adult, idPrefix);
        }

        DrawTags(tags, $"{idPrefix}-tags");
    }

    private void DrawFullProfileBody(
        SnowProfileData profile,
        IDalamudTextureWrap? profileImageTexture,
        IReadOnlyList<UserProfileTagDto> visibleTags,
        string idPrefix)
    {
        var document = profile.Document;
        using (var table = ImRaii.Table($"{idPrefix}-main", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Portrait", ImGuiTableColumnFlags.WidthFixed, 190f);
                ImGui.TableSetupColumn("Profile", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawPortrait(profileImageTexture, 176f, showEmptyLabel: true);
                ImGui.TableNextColumn();
                CharacterProfileUiShared.DrawLabelValue("Pronouns:", document.Pronouns);
                CharacterProfileUiShared.DrawLabelValue("RP status:", document.RpStatus);
                CharacterProfileUiShared.DrawLabelValue("Approach:", document.Approachability);
                DrawAtAGlance(document.AtAGlance);
            }
        }

        DrawBbCodeSection("Overview", document.Overview);
        DrawBbCodeHooks(document.Hooks, idPrefix);
        DrawBbCodeSection("OOC Notes", document.OocNotes);
        if (document.ContentRating == ProfileContentRating.Adult)
        {
            DrawBbCodeSection("Adult Preferences", document.AdultPreferences);
        }
        DrawBoundaries(document.Boundaries, profile.User?.UID, profile.Revision, profile.IsOwnProfile,
            document.ContentRating == ProfileContentRating.Adult, idPrefix);

        DrawTags(visibleTags, $"{idPrefix}-tags");
    }

    private static bool DrawUnavailableProfileMessage(SnowProfileData profile)
    {
        if (profile.Revision <= 0)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                string.IsNullOrWhiteSpace(profile.DisabledReason)
                    ? "This character has not published a profile yet."
                    : profile.DisabledReason);
            return true;
        }

        if (!profile.Disabled)
        {
            return false;
        }

        ImGui.TextColored(ImGuiColors.DalamudRed, profile.DisabledReason);
        return true;
    }

    private static void DrawPortrait(IDalamudTextureWrap? textureWrap, float size, bool showEmptyLabel)
    {
        if (textureWrap == null)
        {
            if (showEmptyLabel)
            {
                ImGui.Dummy(new Vector2(size, size));
                ImGui.TextColored(ImGuiColors.DalamudGrey, "No portrait");
            }

            return;
        }

        var scale = size / MathF.Max(textureWrap.Width, textureWrap.Height);
        ImGui.Image(textureWrap.Handle, new Vector2(textureWrap.Width * scale, textureWrap.Height * scale));
    }

    private static void DrawAtAGlance(IReadOnlyList<string> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        CharacterProfileUiShared.DrawSectionTitle("At A Glance");
        foreach (var entry in entries)
        {
            DrawWrappedBullet(entry);
        }
    }

    private static void DrawWrappedBullet(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }

    private void DrawBbCodeSection(string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        CharacterProfileUiShared.DrawSectionTitle(title);
        using var font = _fontService.GameFont.Push();
        _bbCodeRenderService.Render(text, ImGui.GetContentRegionAvail().X);
    }

    private void DrawBbCodeHooks(IReadOnlyList<CharacterProfileHookDto> hooks, string idPrefix)
    {
        if (hooks.Count == 0)
        {
            return;
        }

        CharacterProfileUiShared.DrawSectionTitle("RP Hooks");
        for (var i = 0; i < hooks.Count; i++)
        {
            var hook = hooks[i];
            using var id = ImRaii.PushId($"{idPrefix}-hook-{i}");
            using var card = ImRaii.Child("hook-card", new Vector2(0f, 112f), true);
            if (!card)
            {
                continue;
            }

            ImGui.TextColored(ImGuiColors.HealerGreen, hook.Title);
            if (!string.IsNullOrWhiteSpace(hook.Description))
            {
                using var font = _fontService.GameFont.Push();
                _bbCodeRenderService.Render(hook.Description, ImGui.GetContentRegionAvail().X);
            }
        }
    }

    private static void DrawPlainHooks(IReadOnlyList<CharacterProfileHookDto> hooks)
    {
        if (hooks.Count == 0)
        {
            return;
        }

        CharacterProfileUiShared.DrawSectionTitle("RP Hooks");
        foreach (var hook in hooks)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, string.IsNullOrWhiteSpace(hook.Title) ? "Hook" : hook.Title);
            if (!string.IsNullOrWhiteSpace(hook.Description))
            {
                ImGui.TextWrapped(hook.Description);
            }

            ImGui.Spacing();
        }
    }

    private static void DrawPlainTextSection(string title, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        CharacterProfileUiShared.DrawSectionTitle(title);
        ImGui.TextWrapped(value);
    }

    private void DrawBoundaries(RpBoundariesDto? boundaries, string? uid, long revision, bool ownProfile, bool adult, string idPrefix)
    {
        if (boundaries == null || (boundaries.Entries.Count == 0 && string.IsNullOrWhiteSpace(boundaries.Note)))
            return;

        var acknowledgementKey = string.IsNullOrWhiteSpace(uid) ? null : uid + ":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var requiresAcknowledgement = adult || boundaries.RequireAcknowledgement;
        CharacterProfileUiShared.DrawSectionTitle("Boundaries");
        var acknowledged = ownProfile || !requiresAcknowledgement
            || acknowledgementKey != null
            && _configService.Current.RpBoundaryAcknowledgements.GetValueOrDefault(acknowledgementKey) >= revision;
        if (!acknowledged)
        {
            using var card = ImRaii.Child(idPrefix + "-boundaries-ack", new Vector2(-1f, 82f * ImGuiHelpers.GlobalScale), true);
            ImGui.TextWrapped("This boundaries card asks you to acknowledge it before viewing.");
            using (ImRaii.Disabled(acknowledgementKey == null))
            {
                if (ImGui.Button("Acknowledge and show", new Vector2(180f * ImGuiHelpers.GlobalScale, 0f)) && acknowledgementKey != null)
                    _configService.Update(config => config.RpBoundaryAcknowledgements[acknowledgementKey] = revision);
            }
            return;
        }

        var generalExpanded = ownProfile || requiresAcknowledgement
            || acknowledgementKey == null || _expandedGeneralBoundaries.Contains(acknowledgementKey);
        if (!generalExpanded)
        {
            using var collapsedCard = ImRaii.Child(idPrefix + "-boundaries-collapsed", new Vector2(-1f, 64f * ImGuiHelpers.GlobalScale), true);
            ImGui.TextWrapped("This profile includes a boundaries card.");
            if (ImGui.Button("Show boundaries", new Vector2(150f * ImGuiHelpers.GlobalScale, 0f)) && acknowledgementKey != null)
                _expandedGeneralBoundaries.Add(acknowledgementKey);
            return;
        }

        var height = (28f + boundaries.Entries.Select(entry => entry.Rating).Distinct().Count() * 25f
            + (string.IsNullOrWhiteSpace(boundaries.Note) ? 0f : 48f)) * ImGuiHelpers.GlobalScale;
        using var boundaryCard = ImRaii.Child(idPrefix + "-boundaries", new Vector2(-1f, height), true);
        foreach (var group in boundaries.Entries.GroupBy(entry => entry.Rating).OrderBy(group => group.Key))
        {
            ImGui.TextColored(BoundaryColour(group.Key), BoundaryLabel(group.Key));
            ImGui.SameLine();
            ImGui.TextWrapped(string.Join("  •  ", group.Select(entry => BoundaryName(entry.Key))));
        }
        if (!string.IsNullOrWhiteSpace(boundaries.Note))
        {
            ImGui.Separator();
            ImGui.TextWrapped(boundaries.Note);
        }
    }

    private static string BoundaryLabel(RpBoundaryRating rating) => rating switch
    {
        RpBoundaryRating.Willing => "Willing",
        RpBoundaryRating.AskFirst => "Ask first",
        RpBoundaryRating.HardNo => "Hard no",
        _ => rating.ToString(),
    };

    private static string BoundaryName(string key) => key switch
    {
        "romance" => "Romance",
        "sexual-themes" => "Sexual themes",
        "violence" => "Violence",
        "injury-gore" => "Injury or gore",
        "horror" => "Horror",
        "death" => "Death",
        "captivity-restraint" => "Captivity or restraint",
        "power-imbalance" => "Power imbalance",
        "substance-use" => "Substance use",
        "discrimination" => "Discrimination",
        "pregnancy-family" => "Pregnancy or family",
        "lore-divergence" => "Lore divergence",
        _ => key,
    };

    private static Vector4 BoundaryColour(RpBoundaryRating rating) => rating switch
    {
        RpBoundaryRating.Willing => ImGuiColors.HealerGreen,
        RpBoundaryRating.AskFirst => ImGuiColors.DalamudYellow,
        RpBoundaryRating.HardNo => ImGuiColors.DalamudRed,
        _ => SnowcloakColours.CompactTextMuted,
    };

    private static void DrawTags(IReadOnlyList<UserProfileTagDto> tags, string idPrefix)
    {
        if (tags.Count == 0)
        {
            return;
        }

        CharacterProfileUiShared.DrawSectionTitle("Tags");
        _ = ProfileTagChipRenderer.DrawTagChips(ProfileTagUtilities.NormalizeForStorage(tags), idPrefix);
    }

    private static void DrawPreviewLabelValue(string label, string? value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }
}

public sealed record ProfileViewRequest(
    SnowProfileData Profile,
    string FallbackName,
    IDalamudTextureWrap? HeaderImageTexture,
    IDalamudTextureWrap? ProfileImageTexture,
    IReadOnlyList<UserProfileTagDto> VisibleTags,
    string? MoodlesData,
    string IdPrefix,
    Action? DrawReportButton = null,
    Action? DrawPairingDetails = null);
