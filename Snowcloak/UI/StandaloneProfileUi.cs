using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Interop.Ipc;
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
    private static readonly TimeSpan LocalMoodlesRefreshInterval = TimeSpan.FromSeconds(2);
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly string _fallbackName;
    private readonly string _ident;
    private readonly IpcManager _ipcManager;
    private readonly Lock _moodlesLock = new();
    private readonly ProfileVisibility? _requestedVisibility;
    private readonly SnowProfileManager _snowProfileManager;
    private readonly UiSharedService _uiSharedService;
    private readonly string _windowIdSuffix;
    private DateTime _lastLocalMoodlesRefreshUtc = DateTime.MinValue;
    private byte[] _lastHeaderImage = [];
    private byte[] _lastProfilePicture = [];
    private string _localMoodlesData = string.Empty;
    private Task? _localMoodlesRefreshTask;
    private IDalamudTextureWrap? _headerTextureWrap;
    private IDalamudTextureWrap? _textureWrap;

    public StandaloneProfileUi(ILogger<StandaloneProfileUi> logger, SnowMediator mediator, UiSharedService uiSharedService,
        SnowProfileManager snowProfileManager, Pair? pair, UserData userData, ProfileVisibility? requestedVisibility,
        string? ident, string? fallbackName, ApiController apiController, DalamudUtilService dalamudUtilService,
        IpcManager ipcManager, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator,
            BuildWindowName(ResolveFallbackName(userData, fallbackName, pair), ident ?? pair?.Ident ?? userData.UID, requestedVisibility),
            performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _snowProfileManager = snowProfileManager;
        _apiController = apiController;
        _dalamudUtilService = dalamudUtilService;
        _ipcManager = ipcManager;
        _fallbackName = ResolveFallbackName(userData, fallbackName, pair);
        Pair = pair;
        UserData = userData;
        _requestedVisibility = requestedVisibility;
        _ident = ident ?? pair?.Ident ?? string.Empty;
        _windowIdSuffix = BuildWindowIdSuffix(ident ?? pair?.Ident ?? userData.UID, requestedVisibility);
        Size = new Vector2(680f, 820f);
        SizeCondition = ImGuiCond.Appearing;
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(560f, 500f),
            MaximumSize = new Vector2(900f, 2000f),
        };
        IsOpen = true;
        Mediator.Subscribe<MoodlesMessage>(this, OnMoodlesChanged);
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
            RefreshHeaderTexture(DecodeImage(profile.Document.HeaderImageBase64));
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
        UpdateWindowTitle(profile);
        CharacterProfileUiShared.DrawHeader(profile.Document, _fallbackName, headerImageTexture: _headerTextureWrap);
        ImGui.Spacing();
        CharacterProfileUiShared.DrawProfileBadges(profile.Document, "standalone-profile-badges");

        DrawReportButton(profile);
        ImGui.SameLine();
        var updated = profile.UpdatedAtUtc.HasValue ? $"  |  updated {profile.UpdatedAtUtc.Value:u}" : string.Empty;
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            $"{profile.Visibility} profile  |  revision {profile.Revision}  |  {profile.Document.ContentRating}{updated}");

        CharacterProfileUiShared.DrawMoodles(GetMoodlesData(profile), "standalone-profile", _uiSharedService);

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
                DrawAtAGlance(profile.Document.AtAGlance);
            }
        }

        DrawBbCodeSection("Overview", profile.Document.Overview);
        DrawHooks(profile.Document.Hooks);
        DrawBbCodeSection("OOC Notes", profile.Document.OocNotes);
        if (profile.Document.ContentRating == ProfileContentRating.Adult)
            DrawBbCodeSection("Adult Preferences", profile.Document.AdultPreferences);
        DrawTags(GetVisibleTagsForViewer(profile));
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

    private void DrawHooks(IReadOnlyList<CharacterProfileHookDto> hooks)
    {
        if (hooks.Count == 0) return;
        CharacterProfileUiShared.DrawSectionTitle("RP Hooks");
        for (var i = 0; i < hooks.Count; i++)
        {
            var hook = hooks[i];
            using var id = ImRaii.PushId($"standalone-profile-hook-{i}");
            using var card = ImRaii.Child("hook-card", new Vector2(0f, 112f), true);
            if (!card)
                continue;

            ImGui.TextColored(ImGuiColors.HealerGreen, hook.Title);
            if (!string.IsNullOrWhiteSpace(hook.Description))
            {
                using var _ = _uiSharedService.GameFont.Push();
                _uiSharedService.RenderBbCode(hook.Description, ImGui.GetContentRegionAvail().X);
            }
        }
    }

    private static void DrawTags(IReadOnlyList<UserProfileTagDto> tags)
    {
        if (tags.Count == 0) return;
        CharacterProfileUiShared.DrawSectionTitle("Tags");
        _ = ProfileTagChipRenderer.DrawTagChips(ProfileTagUtilities.NormalizeForStorage(tags), "standalone-profile-tags");
    }

    private IReadOnlyList<UserProfileTagDto> GetVisibleTagsForViewer(SnowProfileData profile)
    {
        if (profile.IsOwnProfile)
            return ProfileTagUtilities.NormalizeForStorage(profile.Tags);

        var ownProfile = _snowProfileManager.GetOwnProfile(ProfileVisibility.Private);
        var viewerTags = ownProfile.Revision > 0 ? ownProfile.Tags : [];
        return ProfileTagUtilities.GetVisibleTagsForViewer(profile.Tags, viewerTags);
    }

    private string GetMoodlesData(SnowProfileData profile)
    {
        if (!CanShowMoodles())
            return string.Empty;

        var pairMoodles = Pair?.LastReceivedCharacterData?.MoodlesData;
        if (!string.IsNullOrWhiteSpace(pairMoodles))
            return pairMoodles;

        QueueLocalMoodlesRefresh(profile.Ident, force: false);

        lock (_moodlesLock)
        {
            return _localMoodlesData;
        }
    }

    private void OnMoodlesChanged(MoodlesMessage message)
    {
        if (!CanShowMoodles() || message.Address == IntPtr.Zero)
            return;

        var player = _dalamudUtilService.FindPlayerByNameHash(_ident);
        if (player.Address == message.Address)
            QueueLocalMoodlesRefresh(_ident, force: true);
    }

    private bool CanShowMoodles()
        => Pair?.HasAnyConnection() == true;

    private void QueueLocalMoodlesRefresh(string ident, bool force)
    {
        if (string.IsNullOrWhiteSpace(ident) || !_ipcManager.Moodles.APIAvailable || !CanShowMoodles())
        {
            SetLocalMoodlesData(string.Empty);
            return;
        }

        lock (_moodlesLock)
        {
            if (_localMoodlesRefreshTask is { IsCompleted: false })
                return;

            if (!force && DateTime.UtcNow - _lastLocalMoodlesRefreshUtc < LocalMoodlesRefreshInterval)
                return;

            _lastLocalMoodlesRefreshUtc = DateTime.UtcNow;
            _localMoodlesRefreshTask = Task.Run(() => RefreshLocalMoodlesAsync(ident));
        }
    }

    private async Task RefreshLocalMoodlesAsync(string ident)
    {
        try
        {
            var player = _dalamudUtilService.FindPlayerByNameHash(ident);
            if (player.EntityId == 0 || player.Address == IntPtr.Zero)
            {
                SetLocalMoodlesData(string.Empty);
                return;
            }

            SetLocalMoodlesData(await _ipcManager.Moodles.GetStatusAsync(player.Address).ConfigureAwait(false) ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Could not refresh local Moodles for profile {ident}", ident);
            SetLocalMoodlesData(string.Empty);
        }
    }

    private void SetLocalMoodlesData(string moodlesData)
    {
        lock (_moodlesLock)
        {
            _localMoodlesData = moodlesData;
        }
    }

    private void DrawPairingDetails()
    {
        if (Pair == null || !Pair.HasAnyConnection() || !ImGui.CollapsingHeader("Pairing details")) return;
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

    private void RefreshHeaderTexture(byte[] bytes)
    {
        if (_headerTextureWrap != null && bytes.SequenceEqual(_lastHeaderImage)) return;
        _headerTextureWrap?.Dispose();
        _lastHeaderImage = bytes;
        _headerTextureWrap = bytes.Length == 0 ? null : _uiSharedService.LoadImage(bytes);
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    protected override void Dispose(bool disposing)
    {
        _headerTextureWrap?.Dispose();
        _textureWrap?.Dispose();
        base.Dispose(disposing);
    }

    private static byte[] DecodeImage(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return [];
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static string ResolveFallbackName(UserData userData, string? fallbackName, Pair? pair)
    {
        if (!string.IsNullOrWhiteSpace(fallbackName))
            return fallbackName;
        if (pair != null && pair.UserPair == null)
            return pair.IsVisible && !string.IsNullOrWhiteSpace(pair.PlayerName) ? pair.PlayerName : "Unknown character";
        if (pair?.HasAnyConnection() != true)
            return "Unknown character";
        if (!string.IsNullOrWhiteSpace(pair.PlayerName))
            return pair.PlayerName;
        return string.IsNullOrWhiteSpace(userData.AliasOrUID) ? "Unnamed character" : userData.AliasOrUID;
    }

    private void UpdateWindowTitle(SnowProfileData profile)
    {
        var displayName = profile.Revision > 0 && !string.IsNullOrWhiteSpace(profile.Document.CharacterName)
            ? profile.Document.CharacterName
            : _fallbackName;
        WindowName = BuildWindowName(displayName, _windowIdSuffix);
    }

    private static string BuildWindowName(string displayName, string idSuffix)
        => IsPlaceholderTitle(displayName)
            ? "RP Profile" + idSuffix
            : string.Format(CultureInfo.InvariantCulture, "RP Profile: {0}", displayName) + idSuffix;

    private static string BuildWindowName(string displayName, string ident, ProfileVisibility? requestedVisibility)
        => BuildWindowName(displayName, BuildWindowIdSuffix(ident, requestedVisibility));

    private static string BuildWindowIdSuffix(string ident, ProfileVisibility? requestedVisibility)
        => "##SnowcloakSyncStandaloneProfileUI" + ident + requestedVisibility;

    private static bool IsPlaceholderTitle(string? value)
        => string.IsNullOrWhiteSpace(value)
           || string.Equals(value, "Loading RP profile...", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "Loading RP Profile", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "Unnamed character", StringComparison.OrdinalIgnoreCase);
}
