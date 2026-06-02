using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class StandaloneProfileUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly string _fallbackName;
    private readonly string _ident;
    private readonly ProfileVisibility? _requestedVisibility;
    private readonly SnowProfileManager _snowProfileManager;
    private readonly UiSharedService _uiSharedService;
    private byte[] _lastProfilePicture = [];
    private IDalamudTextureWrap? _textureWrap;

    public StandaloneProfileUi(ILogger<StandaloneProfileUi> logger, SnowMediator mediator, UiSharedService uiSharedService,
        SnowProfileManager snowProfileManager, Pair? pair, UserData userData, ProfileVisibility? requestedVisibility,
        string? ident, string? fallbackName, ApiController apiController, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator,
            string.Format(CultureInfo.InvariantCulture, "RP Profile: {0}", ResolveFallbackName(userData, fallbackName))
            + "##SnowcloakSyncStandaloneProfileUI" + (ident ?? pair?.Ident ?? userData.UID) + requestedVisibility,
            performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _snowProfileManager = snowProfileManager;
        _apiController = apiController;
        _fallbackName = ResolveFallbackName(userData, fallbackName);
        Pair = pair;
        UserData = userData;
        _requestedVisibility = requestedVisibility;
        _ident = ident ?? pair?.Ident ?? string.Empty;
        Size = new Vector2(680f, 820f);
        SizeCondition = ImGuiCond.Appearing;
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(560f, 500f),
            MaximumSize = new Vector2(900f, 2000f),
        };
        IsOpen = true;
    }

    public string Ident => _ident;
    public Pair? Pair { get; }
    public UserData UserData { get; }
    public ProfileVisibility? RequestedVisibility => _requestedVisibility;

    protected override void DrawInternal()
    {
        try
        {
            var profile = _snowProfileManager.GetSnowProfile(_ident, _requestedVisibility);
            RefreshTexture(profile.ImageData.Value);
            DrawProfile(profile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not draw standalone RP profile");
        }
    }

    private void DrawProfile(SnowProfileData profile)
    {
        CharacterProfileUiShared.DrawHeader(profile.Document, _fallbackName);
        ImGui.Spacing();

        DrawReportButton(profile);
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            $"{profile.Visibility} profile  |  revision {profile.Revision}  |  {profile.Document.ContentRating}");

        if (profile.Revision <= 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, string.IsNullOrWhiteSpace(profile.DisabledReason)
                ? "This character has not published a profile yet."
                : profile.DisabledReason);
            return;
        }

        if (profile.Disabled)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, profile.DisabledReason);
            return;
        }

        using (var table = ImRaii.Table("rp-profile-main", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Portrait", ImGuiTableColumnFlags.WidthFixed, 190f);
                ImGui.TableSetupColumn("Profile", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawPortrait(176f);
                ImGui.TableNextColumn();
                CharacterProfileUiShared.DrawLabelValue("Pronouns:", profile.Document.Pronouns);
                CharacterProfileUiShared.DrawLabelValue("RP status:", profile.Document.RpStatus);
                CharacterProfileUiShared.DrawLabelValue("Approach:", profile.Document.Approachability);
                CharacterProfileUiShared.DrawLabelValue("Availability:", profile.Document.Availability);
                DrawAtAGlance(profile.Document.AtAGlance);
            }
        }

        DrawBbCodeSection("Overview", profile.Document.Overview);
        DrawHooks(profile.Document.Hooks);
        DrawBbCodeSection("OOC Notes", profile.Document.OocNotes);
        if (profile.Document.ContentRating == ProfileContentRating.Adult)
            DrawBbCodeSection("Adult Preferences", profile.Document.AdultPreferences);
        DrawTags(profile.Tags);
        DrawPairingDetails();
    }

    private void DrawReportButton(SnowProfileData profile)
    {
        var canReport = Pair != null && profile.Revision > 0 && !profile.IsOwnProfile;
        ImGui.BeginDisabled(!canReport);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ExclamationTriangle, "Report displayed profile") && Pair != null)
            Mediator.Publish(new OpenReportPopupMessage(Pair, profile.Ident, profile.Visibility, profile.Revision));
        ImGui.EndDisabled();
    }

    private void DrawPortrait(float size)
    {
        if (_textureWrap == null)
        {
            ImGui.Dummy(new Vector2(size, size));
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No portrait");
            return;
        }

        var scale = size / MathF.Max(_textureWrap.Width, _textureWrap.Height);
        var imageSize = new Vector2(_textureWrap.Width * scale, _textureWrap.Height * scale);
        ImGui.Image(_textureWrap.Handle, imageSize);
    }

    private static void DrawAtAGlance(IReadOnlyList<string> entries)
    {
        if (entries.Count == 0) return;
        CharacterProfileUiShared.DrawSectionTitle("At A Glance");
        foreach (var entry in entries)
            ImGui.BulletText(entry);
    }

    private void DrawBbCodeSection(string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        CharacterProfileUiShared.DrawSectionTitle(title);
        using var _ = _uiSharedService.GameFont.Push();
        _uiSharedService.RenderBbCode(text, ImGui.GetContentRegionAvail().X);
    }

    private void DrawHooks(IReadOnlyList<Snowcloak.API.Dto.User.CharacterProfileHookDto> hooks)
    {
        if (hooks.Count == 0) return;
        CharacterProfileUiShared.DrawSectionTitle("RP Hooks");
        foreach (var hook in hooks)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, hook.Title);
            if (!string.IsNullOrWhiteSpace(hook.Description))
            {
                using var _ = _uiSharedService.GameFont.Push();
                _uiSharedService.RenderBbCode(hook.Description, ImGui.GetContentRegionAvail().X);
            }
            ImGui.Spacing();
        }
    }

    private static void DrawTags(IReadOnlyList<Snowcloak.API.Dto.User.UserProfileTagDto> tags)
    {
        if (tags.Count == 0) return;
        CharacterProfileUiShared.DrawSectionTitle("Tags");
        _ = ProfileTagChipRenderer.DrawTagChips(ProfileTagUtilities.NormalizeForStorage(tags), "standalone-profile-tags");
    }

    private void DrawPairingDetails()
    {
        if (Pair == null || !ImGui.CollapsingHeader("Pairing details")) return;
        var status = Pair.IsVisible ? "Visible" : Pair.IsOnline ? "Online" : "Offline";
        ImGui.TextColored(Pair.IsVisible || Pair.IsOnline ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey, status);
        if (Pair.IsVisible)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"({Pair.PlayerName})");
        }
        if (Pair.UserPair != null)
            ImGui.TextUnformatted("Directly paired");
        if (Pair.GroupPair.Any())
            ImGui.TextUnformatted($"Shared Syncshells: {Pair.GroupPair.Count}");
    }

    private void RefreshTexture(byte[] bytes)
    {
        if (_textureWrap != null && bytes.SequenceEqual(_lastProfilePicture)) return;
        _textureWrap?.Dispose();
        _lastProfilePicture = bytes;
        _textureWrap = bytes.Length == 0 ? null : _uiSharedService.LoadImage(bytes);
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    protected override void Dispose(bool disposing)
    {
        _textureWrap?.Dispose();
        base.Dispose(disposing);
    }

    private static string ResolveFallbackName(UserData userData, string? fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(fallbackName))
            return fallbackName;
        return string.IsNullOrWhiteSpace(userData.AliasOrUID) ? "Unnamed character" : userData.AliasOrUID;
    }
}
