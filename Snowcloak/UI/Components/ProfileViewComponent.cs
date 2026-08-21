using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Dto.Roleplay;
using Snowcloak.Configuration;
using Snowcloak.Core.Roleplay;
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
        if (DrawAdultProfileGate(request, compact: false))
        {
            return;
        }

        CharacterProfileUiShared.DrawHeader(request.Profile.Document, request.FallbackName,
            headerImageTexture: request.HeaderImageTexture, bbCodeRenderService: _bbCodeRenderService);
        ImGui.Spacing();
        CharacterProfileUiShared.DrawProfileBadges(request.Profile.Document, $"{request.IdPrefix}-badges", _bbCodeRenderService);

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

        DrawFullProfileBody(request.Profile, request.ProfileImageTexture, request.VisibleTags, request.IdPrefix,
            request.ViewerPrivateDocument);
        request.DrawPairingDetails?.Invoke();
    }

    public void DrawCompact(ProfileViewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DrawAdultProfileGate(request, compact: true))
        {
            return;
        }

        CharacterProfileUiShared.DrawHeader(
            request.Profile.Document,
            request.FallbackName,
            compact: true,
            headerImageTexture: request.HeaderImageTexture,
            bbCodeRenderService: _bbCodeRenderService);
        CharacterProfileUiShared.DrawProfileBadges(request.Profile.Document, $"{request.IdPrefix}-badges", _bbCodeRenderService);

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
            DrawCompactText(request.Profile.Document.Tagline);
        }

        CharacterProfileUiShared.DrawLabelValue("Approach:", request.Profile.Document.Approachability, _bbCodeRenderService);
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
        CharacterProfileUiShared.DrawHeader(document, fallbackName, headerImageTexture: headerImageTexture,
            bbCodeRenderService: _bbCodeRenderService);
        CharacterProfileUiShared.DrawProfileBadges(document, $"{idPrefix}-badges", _bbCodeRenderService);
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
        DrawPreviewHooks(document.Hooks, idPrefix, allowRichMedia: fullProfile);
        if (fullProfile)
        {
            DrawBbCodeSection("Overview", document.Overview);
            DrawBbCodeSection("OOC Notes", document.OocNotes);
            if (document.ContentRating == ProfileContentRating.Adult)
            {
                DrawBbCodeSection("Adult Preferences", document.AdultPreferences);
            }
            DrawBoundaries(document, null, 0, true,
                document.ContentRating == ProfileContentRating.Adult, idPrefix, null);
        }

        DrawTags(tags, $"{idPrefix}-tags");
    }

    private void DrawFullProfileBody(
        SnowProfileData profile,
        IDalamudTextureWrap? profileImageTexture,
        IReadOnlyList<UserProfileTagDto> visibleTags,
        string idPrefix,
        CharacterProfileDocumentDto? viewerPrivateDocument)
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
                CharacterProfileUiShared.DrawLabelValue("Pronouns:", document.Pronouns, _bbCodeRenderService);
                CharacterProfileUiShared.DrawLabelValue("RP status:", document.RpStatus, _bbCodeRenderService);
                CharacterProfileUiShared.DrawLabelValue("Approach:", document.Approachability, _bbCodeRenderService);
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
        DrawBoundaries(document, profile.User?.UID, profile.Revision, profile.IsOwnProfile,
            adult: false, idPrefix, viewerPrivateDocument);

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

    private bool DrawAdultProfileGate(ProfileViewRequest request, bool compact)
    {
        var profile = request.Profile;
        if (profile.IsOwnProfile || !profile.IsNSFW || profile.Revision <= 0)
        {
            return false;
        }

        var acknowledgementKey = profile.Ident + ":" + profile.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_configService.Current.ProfilesAllowNsfw
            && _configService.Current.RpAdultProfileAcknowledgements.GetValueOrDefault(acknowledgementKey) >= profile.Revision)
        {
            return false;
        }

        using var id = ImRaii.PushId(request.IdPrefix + "-adult-profile-gate");
        AutoSizedCard.Draw(_ =>
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Adult-rated profile");
            if (!_configService.Current.ProfilesAllowNsfw)
            {
                ImGui.TextWrapped("NSFW profiles are disabled. Enable 'Show profiles marked as NSFW' under Settings > Interface > Profiles to view this profile.");
                request.DrawReportButton?.Invoke();
                return;
            }

            ImGui.TextWrapped("This profile is marked Adult and may contain explicit themes. Confirm that you want to reveal it.");
            if (compact)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "Open the full profile to review and confirm.");
                return;
            }

            if (ImGui.Button("Confirm and show profile", new Vector2(210f * ImGuiHelpers.GlobalScale, 0f)))
            {
                _configService.Update(config =>
                    config.RpAdultProfileAcknowledgements[acknowledgementKey] = profile.Revision);
            }
            if (request.DrawReportButton != null)
            {
                ImGui.SameLine();
                request.DrawReportButton();
            }
        });

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

    private void DrawAtAGlance(IReadOnlyList<string> entries)
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

    private void DrawWrappedBullet(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        DrawCompactText(text);
    }

    private void DrawCompactText(string text)
        => _bbCodeRenderService.Render(text, ImGui.GetContentRegionAvail().X, ProfileBbCodeRenderOptions.Compact);

    private void DrawBbCodeSection(string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        CharacterProfileUiShared.DrawSectionTitle(title);
        using var font = _fontService.GameFont.Push();
        _bbCodeRenderService.Render(text, ImGui.GetContentRegionAvail().X, ProfileBbCodeRenderOptions.LongForm);
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
            AutoSizedCard.Draw(innerWidth =>
            {
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
                {
                    _bbCodeRenderService.Render(
                        string.IsNullOrWhiteSpace(hook.Title) ? "Hook" : hook.Title,
                        innerWidth,
                        ProfileBbCodeRenderOptions.Compact);
                }
                if (!string.IsNullOrWhiteSpace(hook.Description))
                {
                    using var font = _fontService.GameFont.Push();
                    _bbCodeRenderService.Render(hook.Description, innerWidth, ProfileBbCodeRenderOptions.LongForm);
                }
            });
            ImGui.Spacing();
        }
    }

    private void DrawPreviewHooks(IReadOnlyList<CharacterProfileHookDto> hooks, string idPrefix, bool allowRichMedia)
    {
        if (hooks.Count == 0)
        {
            return;
        }

        CharacterProfileUiShared.DrawSectionTitle("RP Hooks");
        for (var i = 0; i < hooks.Count; i++)
        {
            var hook = hooks[i];
            using var id = ImRaii.PushId($"{idPrefix}-preview-hook-{i}");
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
            {
                DrawCompactText(string.IsNullOrWhiteSpace(hook.Title) ? "Hook" : hook.Title);
            }
            if (!string.IsNullOrWhiteSpace(hook.Description))
            {
                using var font = _fontService.GameFont.Push();
                _bbCodeRenderService.Render(hook.Description, ImGui.GetContentRegionAvail().X,
                    allowRichMedia ? ProfileBbCodeRenderOptions.LongForm : ProfileBbCodeRenderOptions.Compact);
            }

            ImGui.Spacing();
        }
    }

    private void DrawBoundaries(CharacterProfileDocumentDto document, string? uid, long revision, bool ownProfile,
        bool adult, string idPrefix, CharacterProfileDocumentDto? viewerPrivateDocument)
    {
        var boundaries = document.Boundaries;
        if (ownProfile && (boundaries == null || boundaries.Entries.Count == 0 && string.IsNullOrWhiteSpace(boundaries.Note)))
            return;

        var acknowledgementKey = string.IsNullOrWhiteSpace(uid) ? null : uid + ":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CharacterProfileUiShared.DrawSectionTitle("Boundaries");
        if (boundaries == null || boundaries.Entries.Count == 0 && string.IsNullOrWhiteSpace(boundaries.Note))
        {
            using var id = ImRaii.PushId(idPrefix + "-boundaries-unavailable");
            AutoSizedCard.Draw(_ => DrawBoundaryCompatibility(viewerPrivateDocument, document,
                boundariesShared: false, idPrefix: idPrefix));
            return;
        }

        var requiresAcknowledgement = adult || boundaries.RequireAcknowledgement;
        var acknowledged = ownProfile || !requiresAcknowledgement
            || acknowledgementKey != null
            && _configService.Current.RpBoundaryAcknowledgements.GetValueOrDefault(acknowledgementKey) >= revision;
        if (!acknowledged)
        {
            using var id = ImRaii.PushId(idPrefix + "-boundaries-ack");
            AutoSizedCard.Draw(_ =>
            {
                ImGui.TextWrapped("This boundaries card asks you to acknowledge it before viewing.");
                using (ImRaii.Disabled(acknowledgementKey == null))
                {
                    if (ImGui.Button("Acknowledge and show", new Vector2(180f * ImGuiHelpers.GlobalScale, 0f)) && acknowledgementKey != null)
                        _configService.Update(config => config.RpBoundaryAcknowledgements[acknowledgementKey] = revision);
                }
            });
            return;
        }

        var generalExpanded = ownProfile || requiresAcknowledgement
            || acknowledgementKey == null || _expandedGeneralBoundaries.Contains(acknowledgementKey);
        if (!generalExpanded)
        {
            using var id = ImRaii.PushId(idPrefix + "-boundaries-collapsed");
            AutoSizedCard.Draw(_ =>
            {
                ImGui.TextWrapped("This profile includes a boundaries card.");
                if (ImGui.Button("Show boundaries", new Vector2(150f * ImGuiHelpers.GlobalScale, 0f)) && acknowledgementKey != null)
                    _expandedGeneralBoundaries.Add(acknowledgementKey);
            });
            return;
        }

        using var boundaryId = ImRaii.PushId(idPrefix + "-boundaries");
        AutoSizedCard.Draw(_ =>
        {
            if (!ownProfile)
            {
                DrawBoundaryCompatibility(viewerPrivateDocument, document, boundariesShared: true,
                    idPrefix: idPrefix);
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Their published limits");
            }
            foreach (var group in boundaries.Entries.GroupBy(entry => entry.Rating).OrderBy(group => group.Key))
            {
                ImGui.TextColored(BoundaryColour(group.Key), BoundaryLabel(group.Key));
                ImGui.SameLine();
                ImGui.TextWrapped(string.Join("  •  ", group.Select(entry => BoundaryName(entry.Key))));
            }
            if (!string.IsNullOrWhiteSpace(boundaries.Note))
            {
                ImGui.Separator();
                using var font = _fontService.GameFont.Push();
                _bbCodeRenderService.Render(boundaries.Note, ImGui.GetContentRegionAvail().X,
                    ProfileBbCodeRenderOptions.LongForm);
            }
        });
    }

    private static void DrawBoundaryCompatibility(CharacterProfileDocumentDto? viewerDocument,
        CharacterProfileDocumentDto targetDocument, bool boundariesShared, string idPrefix)
    {
        ImGui.TextColored(SnowcloakColours.OnlineBlue, "Before you start");
        if (viewerDocument == null || viewerDocument.Boundaries == null
            || viewerDocument.Boundaries.Entries.Count == 0)
        {
            ImGui.TextWrapped("Add boundary limits to your own published profile to enable a private comparison.");
            ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                $"Their visible content rating is {targetDocument.ContentRating}.");
            return;
        }

        var comparison = BoundaryCompatibility.Evaluate(
            viewerDocument.Boundaries,
            targetDocument.Boundaries,
            viewerDocument.ContentRating,
            targetDocument.ContentRating);
        var hasConflict = comparison.Conflicts().Any();
        var hasAskFirst = comparison.AskFirst().Any();
        var hasSharedKeys = comparison.Items.Count > 0;
        var overallLabel = hasConflict
            ? "Conflict"
            : hasAskFirst || comparison.ContentRatingMismatch
                ? "Ask first"
                : hasSharedKeys
                    ? "Aligned"
                    : "Not shared";
        var overallColour = hasConflict
            ? ImGuiColors.DalamudRed
            : hasAskFirst || comparison.ContentRatingMismatch
                ? ImGuiColors.DalamudYellow
                : hasSharedKeys
                    ? ImGuiColors.HealerGreen
                    : SnowcloakColours.CompactTextMuted;
        ImGui.TextColored(overallColour, "Compatibility · " + overallLabel);
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            $"Content rating: you {viewerDocument.ContentRating} · they {targetDocument.ContentRating}");
        if (comparison.ContentRatingMismatch)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                "Their visible profile rating exceeds the rating on your profile.");
        }

        if (!boundariesShared)
        {
            ImGui.TextWrapped("This profile has not shared boundary details at your current visibility, so no per-limit comparison is available.");
            ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                "Sharing is optional; the content-rating comparison above is still available.");
            return;
        }

        if (hasSharedKeys)
        {
            using var table = ImRaii.Table(idPrefix + "-boundary-comparison", 4,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH);
            if (table)
            {
                ImGui.TableSetupColumn("Boundary", ImGuiTableColumnFlags.WidthStretch, 1.5f);
                ImGui.TableSetupColumn("You", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Them", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableHeadersRow();
                foreach (var item in comparison.Items)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(BoundaryName(item.Key));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(BoundaryColour(item.Mine), BoundaryLabel(item.Mine));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(BoundaryColour(item.Theirs), BoundaryLabel(item.Theirs));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(CompatibilityColour(item.Kind), CompatibilityLabel(item.Kind));
                }
            }
        }
        else
        {
            ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                "You do not currently share any boundary keys with this profile.");
        }

        if (comparison.MineOnlyKeys.Count > 0)
        {
            ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                "Only on your profile: " + string.Join(", ", comparison.MineOnlyKeys.Select(BoundaryName)));
        }
        if (comparison.TheirsOnlyKeys.Count > 0)
        {
            ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                "Only on their profile: " + string.Join(", ", comparison.TheirsOnlyKeys.Select(BoundaryName)));
        }
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            "Advisory only: compare visible limits and agree before a scene.");
    }

    private static string CompatibilityLabel(BoundaryCompatibilityKind kind) => kind switch
    {
        BoundaryCompatibilityKind.Conflict => "Conflict",
        BoundaryCompatibilityKind.AskFirst => "Ask first",
        _ => "Aligned",
    };

    private static Vector4 CompatibilityColour(BoundaryCompatibilityKind kind) => kind switch
    {
        BoundaryCompatibilityKind.Conflict => ImGuiColors.DalamudRed,
        BoundaryCompatibilityKind.AskFirst => ImGuiColors.DalamudYellow,
        _ => ImGuiColors.HealerGreen,
    };

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

    private void DrawPreviewLabelValue(string label, string? value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.TableNextColumn();
        if (string.IsNullOrWhiteSpace(value))
        {
            ImGui.TextUnformatted("-");
        }
        else
        {
            DrawCompactText(value);
        }
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
    Action? DrawPairingDetails = null,
    CharacterProfileDocumentDto? ViewerPrivateDocument = null);
