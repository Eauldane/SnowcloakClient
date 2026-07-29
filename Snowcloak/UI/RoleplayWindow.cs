using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.Roleplay;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.Core.Chat;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Chat;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Venue;
using Snowcloak.UI.Components;
using Snowcloak.Configuration.Models;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Snowcloak.UI;

public sealed class RoleplayWindow : WindowMediatorSubscriberBase, IStaticWindow
{
    private static readonly Action<ILogger, Exception?> LogDirectoryImageRenderFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(TryGetDirectoryTexture)), "Failed to load roleplay directory image");
    private const string AvailabilityTab = "My availability";
    private const string PeopleTab = "People";
    private const string RoomsTab = "Rooms";
    private const string EventsTab = "Events";
    private readonly RoleplayClientService _roleplay;
    private readonly PairRequestService _pairRequests;
    private readonly ChatClientService _chat;
    private readonly PairManager _pairs;
    private readonly SnowcloakConfigService _config;
    private readonly SnowProfileManager _profiles;
    private readonly ApiController _api;
    private readonly VenueReminderService _venueReminders;
    private readonly UserSafetyStore _safety;
    private readonly TextureService _textureService;
    private readonly ImageTransferService _imageTransferService;
    private readonly Dictionary<string, IDalamudTextureWrap> _bannerTextures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedDirectoryImages = new(StringComparer.Ordinal);
    private string _activeTab = PeopleTab;
    private string _peopleSearch = string.Empty;
    private string _peopleTags = string.Empty;
    private ProfileTagType _peopleTagType = ProfileTagType.Other;
    private string _peopleStatuses = string.Empty;
    private string _peopleApproach = string.Empty;
    private ProfileContentRating? _peopleRating;
    private string _boundaryKey = "romance";
    private bool _boundaryFilterEnabled;
    private RpBoundaryRating _boundaryRating = RpBoundaryRating.Willing;
    private string _roomSearch = string.Empty;
    private string _roomTags = string.Empty;
    private bool _scenesOnly;
    private ProfileContentRating? _roomRating;
    private string _eventSearch = string.Empty;
    private string _eventTags = string.Empty;
    private ProfileContentRating? _eventRating;
    private RpAvailabilityState _availabilityState = RpAvailabilityState.OpenToWalkUps;
    private RpAvailabilityAudience _availabilityAudience = RpAvailabilityAudience.Pairs;
    private int _availabilityTtl = 120;
    private bool _availabilityPaused;
    private string? _availabilityHookId;
    private readonly HashSet<RpTheme> _availabilityThemes = [];
    private readonly HashSet<RpAvailabilityState> _peopleAvailabilityStates = [];
    private readonly HashSet<RpTheme> _peopleThemes = [];
    private readonly HashSet<string> _selectedCurrentHooks = new(StringComparer.Ordinal);
    private int _currentHookTtl = 120;
    private RpAvailabilityAudience _currentHookAudience = RpAvailabilityAudience.Pairs;
    private ProfileReportSurface _reportSurface;
    private string _reportTarget = string.Empty;
    private long _reportRevision;
    private string _reportReason = string.Empty;
    private bool _reportBlockOwner;
    private bool _openReportPopup;
    private bool _hooksInitialised;
    private string _status = string.Empty;
    private int _peoplePage;
    private int _roomPage;
    private int _eventPage;
    private Task<SnowProfileData>? _profileEligibilityTask;
    private const int PageSize = 50;

    public RoleplayWindow(ILogger<RoleplayWindow> logger, SnowMediator mediator,
        RoleplayClientService roleplay, PairRequestService pairRequests, ChatClientService chat,
        PairManager pairs, SnowcloakConfigService config, SnowProfileManager profiles, ApiController api,
        VenueReminderService venueReminders, UserSafetyStore safety, TextureService textureService,
        ImageTransferService imageTransferService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Roleplay###SnowcloakRoleplay", performanceCollectorService)
    {
        _roleplay = roleplay;
        _pairRequests = pairRequests;
        _chat = chat;
        _pairs = pairs;
        _config = config;
        _profiles = profiles;
        _api = api;
        _venueReminders = venueReminders;
        _safety = safety;
        _textureService = textureService;
        _imageTransferService = imageTransferService;
        SetScaledSizeConstraints(new Vector2(700f, 520f), new Vector2(1400f, 1800f));
        Size = new Vector2(900f, 720f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var texture in _bannerTextures.Values)
                texture.Dispose();
            _bannerTextures.Clear();
            _failedDirectoryImages.Clear();
        }
        base.Dispose(disposing);
    }

    public override void OnOpen()
    {
        LoadAvailabilityDraft();
        _hooksInitialised = false;
        _selectedCurrentHooks.Clear();
        StartEligibilityCheck();
        _safety.EnsureLoaded();
        _ = _roleplay.RefreshAsync();
    }

    protected override void DrawInternal()
    {
        SnowcloakUi.AccentColor = SnowcloakColours.OnlineBlue;
        if (!DrawEligibility()) return;
        _activeTab = ModernTabBar.Draw("roleplay-tabs", [AvailabilityTab, PeopleTab, RoomsTab, EventsTab], _activeTab);
        ImGuiHelpers.ScaledDummy(6f);
        if (!string.IsNullOrWhiteSpace(_roleplay.Status))
            ImGui.TextColored(ImGuiColors.DalamudYellow, _roleplay.Status);
        if (!string.IsNullOrWhiteSpace(_status))
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, _status);
        using var disabled = ImRaii.Disabled(!_roleplay.IsBusy && !_roleplayIsSupported());
        if (string.Equals(_activeTab, AvailabilityTab, StringComparison.Ordinal))
            DrawAvailabilityCard();
        else if (string.Equals(_activeTab, PeopleTab, StringComparison.Ordinal))
            DrawPeople();
        else if (string.Equals(_activeTab, RoomsTab, StringComparison.Ordinal))
            DrawRooms();
        else
            DrawEvents();
        DrawReportPopup();
    }

    private bool _roleplayIsSupported() => string.IsNullOrWhiteSpace(_roleplay.Status)
        || !_roleplay.Status.Contains("does not support", StringComparison.OrdinalIgnoreCase);

    private void DrawPeople()
    {
        DrawPeopleFilters();
        DrawBlockedUsers();
        DrawPagination(_peoplePage, _roleplay.People.TotalCount, page =>
        {
            _peoplePage = page;
            SearchPeople();
        });
        using var list = ImRaii.Child("rp-people-results", new Vector2(-1f, -1f), false);
        foreach (var entry in _roleplay.People.Entries)
        {
            DrawPersonCard(entry);
            ImGuiHelpers.ScaledDummy(6f);
        }
        if (_roleplay.People.Entries.Count == 0)
            DrawEmpty("No matching RP profiles", FontAwesomeIcon.UserFriends);
    }

    private void DrawAvailabilityCard()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var scale = ImGuiHelpers.GlobalScale;
        if (width < 800f * scale)
        {
            DrawAvailabilityEditor(new Vector2(-1f, -1f));
            return;
        }

        var gap = 8f * scale;
        var previewWidth = Math.Clamp(width * 0.34f, 260f * scale, 320f * scale);
        DrawAvailabilityEditor(new Vector2(width - previewWidth - gap, -1f));
        ImGui.SameLine(0f, gap);
        DrawAvailabilityPreview(new Vector2(previewWidth, -1f));
    }

    private void DrawAvailabilityEditor(Vector2 size)
    {
        using var panelBackground = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var panelPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 10f) * ImGuiHelpers.GlobalScale);
        using var panel = ImRaii.Child("rp-availability-editor", size, true);
        if (!panel)
            return;

        ModernSection.Header(FontAwesomeIcon.CommentDots, "My RP availability");
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Publish a temporary profile card so compatible players can find you.");

        var listed = _roleplay.Consent.Listed;
        if (ImGui.Checkbox("List my public profile in RP discovery", ref listed))
            Queue(_roleplay.SetConsentAsync(listed), "Directory preference saved.");
        ImGui.SameLine();
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, listed ? "Your public profile can appear in searches." : "Your profile is hidden from RP searches.");

        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Availability");
        ImGui.SameLine(0f, 12f * ImGuiHelpers.GlobalScale);
        DrawChoiceChipRow("rp-availability-state", Enum.GetValues<RpAvailabilityState>(),
            AvailabilityStateLabel, state => state == _availabilityState, state => _availabilityState = state);

        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Visible to");
        ImGui.SameLine(0f, 12f * ImGuiHelpers.GlobalScale);
        DrawChoiceChipRow("rp-availability-audience", Enum.GetValues<RpAvailabilityAudience>(),
            AudienceLabel, audience => audience == _availabilityAudience, audience => _availabilityAudience = audience);

        DrawAvailabilityDurationPicker();
        ImGui.SameLine();
        if (DrawChoiceChip("rp-availability-paused", "Paused", _availabilityPaused))
            _availabilityPaused = !_availabilityPaused;

        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Themes");
        ImGui.SameLine(0f, 12f * ImGuiHelpers.GlobalScale);
        DrawChoiceChipRow("rp-availability-theme", Enum.GetValues<RpTheme>(), ThemeLabel,
            _availabilityThemes.Contains, theme => ToggleSetValue(_availabilityThemes, theme));

        var hooks = _roleplay.CurrentHooks.Hooks;
        ImGui.SetNextItemWidth(MathF.Min(340f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
        var hookLabel = hooks.FirstOrDefault(hook => string.Equals(hook.HookId, _availabilityHookId, StringComparison.Ordinal))?.Title ?? "No current hook";
        if (ImGui.BeginCombo("##rp-availability-hook", hookLabel))
        {
            if (ImGui.Selectable("No current hook", _availabilityHookId == null)) _availabilityHookId = null;
            foreach (var hook in hooks)
                if (ImGui.Selectable(hook.Title, string.Equals(hook.HookId, _availabilityHookId, StringComparison.Ordinal))) _availabilityHookId = hook.HookId;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Publish availability"))
            Queue(_roleplay.SetAvailabilityAsync(new RpAvailabilityCardUpdateDto
            {
                State = _availabilityState,
                Audience = _availabilityAudience,
                Themes = _availabilityThemes.Order().ToList(),
                TtlMinutes = _availabilityTtl,
                Paused = _availabilityPaused,
                CurrentHookId = _availabilityHookId,
            }), "RP availability published.", LoadAvailabilityDraft);
        ImGui.SameLine();
        using (ImRaii.Disabled(_roleplay.OwnAvailability == null))
            if (ImGui.Button("Clear")) Queue(_roleplay.ClearAvailabilityAsync(), "RP availability cleared.", LoadAvailabilityDraft);

        DrawCurrentHookEditor();
    }

    private void DrawAvailabilityPreview(Vector2 size)
    {
        using var panelBackground = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var panelPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f) * ImGuiHelpers.GlobalScale);
        using var panel = ImRaii.Child("rp-availability-preview", size, true);
        if (!panel)
            return;

        ModernSection.Header(FontAwesomeIcon.Eye, "Availability preview");
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "How your listing appears.");
        ImGuiHelpers.ScaledDummy(6f);
        DrawAvailabilityPreviewCard();
    }

    private void DrawAvailabilityPreviewCard()
    {
        var profile = _profiles.GetOwnProfile(ProfileVisibility.Public);
        var document = profile.Document;
        var hook = _roleplay.CurrentHooks.Hooks.FirstOrDefault(candidate =>
            string.Equals(candidate.HookId, _availabilityHookId, StringComparison.Ordinal));
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var cardMin = ImGui.GetCursorScreenPos();
        var cardWidth = ImGui.GetContentRegionAvail().X;
        var padding = new Vector2(12f, 10f) * scale;
        var innerWidth = cardWidth - padding.X * 2f;
        var active = !_availabilityPaused && _availabilityState is not RpAvailabilityState.Closed;
        var statusColour = active ? ImGuiColors.HealerGreen : SnowcloakColours.CompactTextMuted;

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(cardMin + padding);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + innerWidth);

        ImGui.TextColored(SnowcloakColours.OnlineBlue,
            string.IsNullOrWhiteSpace(document.CharacterName) ? "Unnamed character" : document.CharacterName);
        ImGui.SameLine();
        ImGui.TextColored(statusColour, _availabilityPaused ? "Paused" : AvailabilityStateLabel(_availabilityState));
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            AudienceLabel(_availabilityAudience) + "  |  " + FormatDuration(_availabilityTtl));

        if (!string.IsNullOrWhiteSpace(document.RpStatus))
            ImGui.TextColored(ImGuiColors.HealerGreen, document.RpStatus);
        if (!string.IsNullOrWhiteSpace(document.Tagline))
            ImGui.TextWrapped(document.Tagline);
        if (!string.IsNullOrWhiteSpace(document.Approachability))
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Approach: " + document.Approachability);
        if (_availabilityThemes.Count > 0)
            ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                string.Join("   ", _availabilityThemes.Order().Select(theme => "#" + ThemeLabel(theme))));

        if (hook != null)
        {
            ImGui.Separator();
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Current hook");
            ImGui.TextUnformatted(hook.Title);
            if (!string.IsNullOrWhiteSpace(hook.Description))
                ImGui.TextWrapped(hook.Description);
        }

        ImGui.Separator();
        ImGui.TextColored(_roleplay.Consent.Listed ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow,
            _roleplay.Consent.Listed ? "Listed in RP discovery" : "Not listed in RP discovery");

        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var contentMax = ImGui.GetItemRectMax();
        var cardMax = new Vector2(cardMin.X + cardWidth, contentMax.Y + padding.Y);
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(cardMin, cardMax, Colour.Vector4ToColour(SnowcloakColours.CompactPanelAlt), 4f * scale);
        drawList.AddLine(cardMin, cardMin with { Y = cardMax.Y }, Colour.Vector4ToColour(SnowcloakColours.OnlineBlue), 2f * scale);
        drawList.AddRect(cardMin, cardMax, Colour.Vector4ToColour(SnowcloakColours.CompactBorderSubtle),
            4f * scale, ImDrawFlags.None, scale);
        drawList.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(cardMin.X, cardMax.Y));
    }

    private void DrawAvailabilityDurationPicker()
    {
        ImGui.SetNextItemWidth(150f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Availability duration", FormatDuration(_availabilityTtl)))
        {
            foreach (var minutes in new[] { 30, 60, 120, 240, 480, 1440 })
            {
                if (ImGui.Selectable(FormatDuration(minutes), _availabilityTtl == minutes))
                    _availabilityTtl = minutes;
            }
            ImGui.EndCombo();
        }
        ElezenImgui.AttachTooltip("How long this availability card remains visible before it expires automatically.");
    }

    private void DrawCurrentHookEditor()
    {
        if (!_hooksInitialised && _roleplay.CurrentHooks.Hooks.Count > 0)
        {
            _selectedCurrentHooks.UnionWith(_roleplay.CurrentHooks.Hooks.Select(hook => hook.HookId));
            _hooksInitialised = true;
        }
        var profile = _profiles.GetOwnProfile(ProfileVisibility.Private);
        if (profile.Document.Hooks.Count == 0) return;
        ImGuiHelpers.ScaledDummy(4f);
        if (!ImGui.CollapsingHeader("Share profile hooks"))
            return;
        DrawChoiceChipRow("rp-current-hook", profile.Document.Hooks.Take(8).ToArray(), hook => hook.Title,
            hook => _selectedCurrentHooks.Contains(hook.HookId), hook => ToggleSetValue(_selectedCurrentHooks, hook.HookId));
        ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Hook TTL", ref _currentHookTtl, 15, 60);
        _currentHookTtl = Math.Clamp(_currentHookTtl, 5, 1440);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f * ImGuiHelpers.GlobalScale);
        DrawEnumCombo("##current-hook-audience", ref _currentHookAudience, AudienceLabel,
            audience => audience != RpAvailabilityAudience.LocalOnly);
        ImGui.SameLine();
        if (ImGui.Button("Publish current hooks"))
            Queue(_roleplay.SetCurrentHooksAsync(new RpCurrentHooksUpdateDto
            {
                Hooks = _selectedCurrentHooks.Select(id => new RpCurrentHookSelectionDto
                {
                    HookId = id,
                    TtlMinutes = _currentHookTtl,
                    Audience = _currentHookAudience,
                }).ToList(),
            }), "Current hooks published.");
    }

    private void DrawPeopleFilters()
    {
        using var panelBackground = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var panelPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 10f) * ImGuiHelpers.GlobalScale);
        using var panel = ImRaii.Child("rp-people-filters", new Vector2(-1f, 225f * ImGuiHelpers.GlobalScale), true);
        if (!panel)
            return;

        ModernSection.Header(FontAwesomeIcon.Search, "Find people");
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(width * 0.38f);
        ImGui.InputTextWithHint("##rp-people-search", "Search profile text", ref _peopleSearch, 120);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width * 0.16f);
        DrawEnumCombo("##rp-people-tag-type", ref _peopleTagType, ProfileTagTypeLabel);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width * 0.18f);
        ImGui.InputTextWithHint("##rp-people-tags", "Tags, comma-separated", ref _peopleTags, 160);
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Search, "Search RP directory"))
        {
            _peoplePage = 0;
            SearchPeople();
        }

        ImGui.SetNextItemWidth(width * 0.25f);
        ImGui.InputTextWithHint("##rp-status-filter", "RP statuses", ref _peopleStatuses, 120);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width * 0.25f);
        ImGui.InputTextWithHint("##rp-approach-filter", "Approachability", ref _peopleApproach, 120);
        ImGui.SameLine();
        DrawRatingFilter("##rp-people-rating", ref _peopleRating);

        if (DrawChoiceChip("rp-boundary-enabled", "Boundary filter", _boundaryFilterEnabled))
            _boundaryFilterEnabled = !_boundaryFilterEnabled;
        if (_boundaryFilterEnabled)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f * ImGuiHelpers.GlobalScale);
            if (ImGui.BeginCombo("##rp-boundary-key", BoundaryKeyLabel(_boundaryKey)))
            {
                foreach (var key in BoundaryKeys)
                    if (ImGui.Selectable(BoundaryKeyLabel(key), string.Equals(key, _boundaryKey, StringComparison.Ordinal)))
                        _boundaryKey = key;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
            DrawEnumCombo("##rp-boundary-rating", ref _boundaryRating, BoundaryLabel);
        }

        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Availability");
        ImGui.SameLine(0f, 12f * ImGuiHelpers.GlobalScale);
        DrawChoiceChipRow("rp-people-state", Enum.GetValues<RpAvailabilityState>(), AvailabilityStateLabel,
            _peopleAvailabilityStates.Contains, state => ToggleSetValue(_peopleAvailabilityStates, state));
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Themes");
        ImGui.SameLine(0f, 12f * ImGuiHelpers.GlobalScale);
        DrawChoiceChipRow("rp-people-theme", Enum.GetValues<RpTheme>(), ThemeLabel,
            _peopleThemes.Contains, theme => ToggleSetValue(_peopleThemes, theme));
    }

    private void SearchPeople()
    {
        var tags = SplitTerms(_peopleTags).Select(value => new UserProfileTagDto(_peopleTagType, value)).ToList();
        Queue(_roleplay.SearchPeopleAsync(new RpProfileDirectoryQueryDto
        {
            Search = EmptyToNull(_peopleSearch),
            Tags = tags,
            RpStatuses = SplitTerms(_peopleStatuses),
            Approachability = SplitTerms(_peopleApproach),
            ContentRating = _peopleRating,
            Boundaries = !_boundaryFilterEnabled ? [] : [new RpBoundaryEntryDto { Key = _boundaryKey, Rating = _boundaryRating }],
            AvailabilityStates = [.. _peopleAvailabilityStates],
            Themes = [.. _peopleThemes],
            Skip = _peoplePage * PageSize,
            Take = PageSize,
        }), "RP directory refreshed.");
    }

    private void DrawPersonCard(RpProfileDirectoryEntryDto entry)
    {
        var profile = entry.Profile;
        using var id = ImRaii.PushId(profile.Ident);
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanelAlt);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 9f) * ImGuiHelpers.GlobalScale);
        var height = (entry.Boundaries == null ? 142f : 166f) * ImGuiHelpers.GlobalScale;
        using var card = ImRaii.Child("rp-person-card", new Vector2(-1f, height), true);
        if (!string.IsNullOrWhiteSpace(profile.ProfilePictureHash))
        {
            DrawProfilePortrait(profile.ProfilePictureHash);
            ImGui.SameLine(0f, 12f * ImGuiHelpers.GlobalScale);
        }
        ImGui.BeginGroup();
        ImGui.TextColored(SnowcloakColours.OnlineBlue, string.IsNullOrWhiteSpace(profile.CharacterName) ? "Unnamed character" : profile.CharacterName);
        if (!string.IsNullOrWhiteSpace(profile.RpStatus))
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.HealerGreen, profile.RpStatus);
        }
        if (!string.IsNullOrWhiteSpace(profile.Tagline)) ImGui.TextWrapped(profile.Tagline);
        if (!string.IsNullOrWhiteSpace(profile.Approachability)) ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Approach: " + profile.Approachability);
        if (profile.CurrentHook != null)
            ImGui.TextWrapped("Current hook: " + profile.CurrentHook.Title);
        if (entry.Availability != null)
            ImGui.TextColored(ImGuiColors.HealerGreen, AvailabilityStateLabel(entry.Availability.State) + " · " + string.Join(", ", entry.Availability.Themes.Select(ThemeLabel)));
        if (profile.Tags.Count > 0)
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, string.Join("   ", profile.Tags.Take(6).Select(tag => tag.Value)));
        if (entry.Boundaries?.Entries.Count > 0)
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Boundaries available on the full profile");

        if (ImGui.Button("View profile")) Queue(_pairRequests.RequestProfileAsync(profile.Ident), string.Empty);
        ImGui.SameLine();
        var targetIntro = CreateIntro(profile.CurrentHook);
        var ownIntro = CreateIntro(_roleplay.CurrentHooks.Hooks.FirstOrDefault());
        var targetUid = profile.User?.UID;
        var pair = string.IsNullOrWhiteSpace(targetUid)
            ? null
            : _pairs.DirectPairs.FirstOrDefault(candidate =>
                string.Equals(candidate.UserData.UID, targetUid, StringComparison.Ordinal));
        if (pair == null)
        {
            if (ImGui.Button(ownIntro == null ? "Register interest" : "Interest + my hook"))
                Queue(_pairRequests.SendPairRequestAsync(profile.Ident, ownIntro, PairingRequestSource.RoleplayDirectory), "Interest request sent.");
            ElezenImgui.AttachTooltip("Sends a pair request marked as coming from the RP directory. Direct messages become available after it is accepted.");
        }
        else if (!pair.IsMutualDirectPair)
        {
            using (ImRaii.Disabled())
            {
                ImGui.Button("Awaiting mutual pairing");
            }
        }
        else
        {
            if (ImGui.Button(targetIntro == null ? "Open DM" : "Message about hook")) OpenDirectMessage(pair, targetIntro);
        }
        ImGui.SameLine();
        if (ImGui.Button("Report / block")) OpenReport(ProfileReportSurface.Directory, profile.Ident, profile.Revision);
        ImGui.EndGroup();
    }

    private void DrawRooms()
    {
        DrawPendingRoomInvite();
        ModernSection.Header(FontAwesomeIcon.DoorOpen, "Public RP rooms");
        var width = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth((width - spacing) * 0.62f);
        ImGui.InputTextWithHint("##rp-room-search", "Search rooms, scenes and settings", ref _roomSearch, 120);
        ImGui.SameLine();
        ImGui.SetNextItemWidth((width - spacing) * 0.38f);
        ImGui.InputTextWithHint("##rp-room-tags", "Tags", ref _roomTags, 120);

        ImGui.Checkbox("Scenes only", ref _scenesOnly);
        ImGui.SameLine();
        DrawRatingFilter("##rp-room-rating", ref _roomRating);
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Search, "Search rooms"))
        {
            _roomPage = 0;
            SearchRooms();
        }

        ImGuiHelpers.ScaledDummy(8f);
        DrawPagination(_roomPage, _roleplay.Rooms.TotalCount, page =>
        {
            _roomPage = page;
            SearchRooms();
        });
        using var list = ImRaii.Child("rp-room-results", new Vector2(-1f, -1f), false);
        foreach (var entry in _roleplay.Rooms.Entries)
        {
            DrawRoomCard(entry);
            ImGuiHelpers.ScaledDummy(6f);
        }
        if (_roleplay.Rooms.Entries.Count == 0) DrawEmpty("No matching public rooms", FontAwesomeIcon.DoorClosed);
    }

    private void DrawPendingRoomInvite()
    {
        foreach (var invite in _roleplay.PendingInvites)
        {
            using var id = ImRaii.PushId(invite.Room.RoomId + invite.Inviter.UID);
            using var background = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanelAlt);
            using var panel = ImRaii.Child("rp-room-invite", new Vector2(-1f, 92f * ImGuiHelpers.GlobalScale), true);
            ImGui.TextColored(SnowcloakColours.OnlineBlue, "Room invitation from " + invite.Inviter.AliasOrUID);
            if (invite.Intro != null)
                ImGui.TextWrapped(invite.Intro.Title + " — " + invite.Intro.Description);
            if (ImGui.Button("Join room"))
            {
                Queue(JoinRoomAsync(invite.Room), "Room opened.");
                _roleplay.DismissInvite(invite);
            }
            ImGui.SameLine();
            if (ImGui.Button("Dismiss")) _roleplay.DismissInvite(invite);
            ModernSection.SoftSeparator();
        }
    }

    private void DrawRoomCard(RoomDirectoryEntryDto entry)
    {
        var room = entry.Room;
        using var id = ImRaii.PushId(room.RoomId);
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var cardMin = ImGui.GetCursorScreenPos();
        var cardWidth = ImGui.GetContentRegionAvail().X;
        var padding = new Vector2(12f, 9f) * scale;
        var innerWidth = cardWidth - padding.X * 2f;

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(cardMin + padding);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + innerWidth);

        ImGui.TextColored(SnowcloakColours.OnlineBlue, room.Scene?.IsScene == true && !string.IsNullOrWhiteSpace(room.Scene.Title) ? room.Scene.Title : room.Name);
        ImGui.SameLine();
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, $"{entry.UserCount} members");
        if (!string.IsNullOrWhiteSpace(room.Discovery?.BannerImageHash))
            DrawBannerImage(room.Discovery.BannerImageHash);
        if (!string.IsNullOrWhiteSpace(room.Topic)) ImGui.TextWrapped(room.Topic);
        if (room.Scene?.IsScene == true)
        {
            if (!string.IsNullOrWhiteSpace(room.Scene.Setting))
            {
                using var colour = ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted);
                ImGui.TextWrapped("Setting: " + room.Scene.Setting);
            }
            if (room.Scene.ContentWarnings.Count > 0)
            {
                using var colour = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                ImGui.TextWrapped("Warnings: " + string.Join(", ", room.Scene.ContentWarnings));
            }
        }
        if (room.Discovery?.Tags.Count > 0)
        {
            using var colour = ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted);
            ImGui.TextWrapped(string.Join("   ", room.Discovery.Tags.Select(tag => "#" + tag)));
        }
        if (ImGui.Button("Join and open")) Queue(JoinRoomAsync(room), "Room opened.");
        ImGui.SameLine();
        if (ImGui.Button("Report / block")) OpenReport(ProfileReportSurface.Room, room.RoomId);

        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var contentMax = ImGui.GetItemRectMax();
        var cardMax = new Vector2(cardMin.X + cardWidth, contentMax.Y + padding.Y);
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(cardMin, cardMax, Colour.Vector4ToColour(SnowcloakColours.CompactPanelAlt), 4f * scale);
        drawList.AddRect(cardMin, cardMax, Colour.Vector4ToColour(SnowcloakColours.CompactBorderSubtle),
            4f * scale, ImDrawFlags.None, scale);
        drawList.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(cardMin.X, cardMax.Y));
    }

    private async Task JoinRoomAsync(Snowcloak.API.Data.RoomData room)
    {
        if (await _chat.JoinRoomAsync(room).ConfigureAwait(false))
        {
            var key = new ConversationKey(ConversationKind.Room, room.RoomId);
            Mediator.Publish(new OpenChatConversationMessage(key));
        }
    }

    private void DrawEvents()
    {
        ModernSection.Header(FontAwesomeIcon.CalendarDay, "RP agenda");
        var width = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth((width - spacing) * 0.62f);
        ImGui.InputTextWithHint("##rp-event-search", "Search public events", ref _eventSearch, 120);
        ImGui.SameLine();
        ImGui.SetNextItemWidth((width - spacing) * 0.38f);
        ImGui.InputTextWithHint("##rp-event-tags", "Theme tags", ref _eventTags, 120);

        DrawRatingFilter("##rp-event-rating", ref _eventRating);
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Search, "Search public events"))
        {
            _eventPage = 0;
            SearchEvents();
        }

        var joinedKeys = _roleplay.JoinedEvents.Select(EventKey).ToHashSet(StringComparer.Ordinal);
        var events = _roleplay.JoinedEvents.Concat(_roleplay.PublicEvents.Entries.Where(entry => !joinedKeys.Contains(EventKey(entry))))
            .GroupBy(EventKey, StringComparer.Ordinal)
            .Select(group => group.OrderBy(entry => entry.Event.StartsAtUtc).First())
            .Where(entry => string.IsNullOrWhiteSpace(_eventSearch)
                || entry.Event.Title.Contains(_eventSearch, StringComparison.OrdinalIgnoreCase)
                || (entry.Event.Description?.Contains(_eventSearch, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(entry => _eventRating == null || entry.Event.ContentRating == _eventRating)
            .Where(entry => SplitTerms(_eventTags).All(tag => entry.Event.ThemeTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.Event.StartsAtUtc)
            .ToList();
        ImGuiHelpers.ScaledDummy(8f);
        DrawPagination(_eventPage, _roleplay.PublicEvents.TotalCount, page =>
        {
            _eventPage = page;
            SearchEvents();
        });
        using var list = ImRaii.Child("rp-event-results", new Vector2(-1f, -1f), false);
        foreach (var venue in _roleplay.VisibleVenueEvents.Where(MatchesVenueEventFilters))
        {
            DrawVenueEventCard(venue, _venueReminders.IsEventBookmarked(venue.Advertisement.Id));
            ImGuiHelpers.ScaledDummy(6f);
        }
        foreach (var entry in events)
        {
            DrawEventCard(entry, joinedKeys.Contains(EventKey(entry)));
            ImGuiHelpers.ScaledDummy(6f);
        }
        if (events.Count == 0 && !_roleplay.VisibleVenueEvents.Any(MatchesVenueEventFilters))
            DrawEmpty("No upcoming RP events", FontAwesomeIcon.CalendarTimes);
    }

    private void DrawVenueEventCard(RoleplayVenueEvent item, bool reminded)
    {
        var venue = item.Venue;
        var advertisement = item.Advertisement;
        using var id = ImRaii.PushId(advertisement.Id.ToString("N"));
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanelAlt);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 9f) * ImGuiHelpers.GlobalScale);
        var height = string.IsNullOrWhiteSpace(advertisement.BannerFileHash) ? 86f : 164f;
        using var card = ImRaii.Child("rp-venue-event-card", new Vector2(-1f, height * ImGuiHelpers.GlobalScale), true);
        ImGui.TextColored(SnowcloakColours.OnlineBlue, venue.VenueName);
        ImGui.SameLine();
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, advertisement.StartsAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Upcoming venue slot");
        if (!string.IsNullOrWhiteSpace(advertisement.BannerFileHash)) DrawBannerImage(advertisement.BannerFileHash);
        if (!string.IsNullOrWhiteSpace(advertisement.Text)) ImGui.TextWrapped(advertisement.Text);
        var reminder = reminded;
        if (ImGui.Checkbox("Remind me##venue", ref reminder))
        {
            if (reminder) _venueReminders.AddEventBookmark(venue, advertisement);
            else _venueReminders.RemoveEventBookmark(advertisement.Id);
        }
    }

    private void DrawEventCard(RpEventDirectoryEntryDto entry, bool joined)
    {
        var shellEvent = entry.Event;
        using var id = ImRaii.PushId(EventKey(entry));
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanelAlt);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 9f) * ImGuiHelpers.GlobalScale);
        var height = string.IsNullOrWhiteSpace(shellEvent.BannerImageHash) ? 158f : 236f;
        using var card = ImRaii.Child("rp-event-card", new Vector2(-1f, height * ImGuiHelpers.GlobalScale), true);
        ImGui.TextColored(SnowcloakColours.OnlineBlue, shellEvent.Title);
        ImGui.SameLine();
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, shellEvent.StartsAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        if (shellEvent.Recurrence != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, FormatEventRecurrence(shellEvent.Recurrence));
        }
        if (joined)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.HealerGreen, "Joined syncshell");
        }
        if (!string.IsNullOrWhiteSpace(shellEvent.BannerImageHash)) DrawBannerImage(shellEvent.BannerImageHash);
        if (!string.IsNullOrWhiteSpace(shellEvent.Description)) ImGui.TextWrapped(shellEvent.Description);
        var location = string.Join("  •  ", new[] { shellEvent.Plot, shellEvent.WorldId?.ToString(CultureInfo.InvariantCulture) }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrEmpty(location)) ImGui.TextColored(SnowcloakColours.CompactTextMuted, location);
        if (shellEvent.ThemeTags.Count > 0) ImGui.TextColored(SnowcloakColours.CompactTextMuted, string.Join("   ", shellEvent.ThemeTags.Select(tag => "#" + tag)));
        if (shellEvent.Hosts.Count > 0) ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Hosts: " + string.Join(", ", shellEvent.Hosts));
        if (shellEvent.ContentWarnings.Count > 0) ImGui.TextColored(ImGuiColors.DalamudYellow, "Warnings: " + string.Join(", ", shellEvent.ContentWarnings));
        if (shellEvent.Capacity.HasValue) ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Capacity: " + shellEvent.Capacity.Value.ToString(CultureInfo.InvariantCulture));
        var reminder = _config.Current.RpEventReminders.Contains(shellEvent.Id);
        if (ImGui.Checkbox("Remind me", ref reminder))
            _config.Update(config =>
            {
                if (reminder) config.RpEventReminders.Add(shellEvent.Id);
                else config.RpEventReminders.Remove(shellEvent.Id);
            });
        ImGui.SameLine();
        if (ImGui.Button("Copy iCalendar"))
        {
            ImGui.SetClipboardText(BuildICalendar(entry));
            _status = "iCalendar event copied to the clipboard.";
        }
        var group = _pairs.Groups.Values.FirstOrDefault(candidate => string.Equals(candidate.GID, entry.Group.GID, StringComparison.Ordinal));
        if (group != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Open syncshell events")) Mediator.Publish(new OpenSyncshellEventsWindow(group));
        }
        else
        {
            ImGui.SameLine();
            if (ImGui.Button("Join syncshell"))
                Queue(JoinEventSyncshellAsync(entry), $"Joined {entry.Group.AliasOrGID}.");
            ElezenImgui.AttachTooltip("Public events allow eligible players to join their syncshell without a password.");
        }
        ImGui.SameLine();
        if (ImGui.Button("Report / block")) OpenReport(ProfileReportSurface.Event, shellEvent.Id.ToString("D"));
    }

    private async Task JoinEventSyncshellAsync(RpEventDirectoryEntryDto entry)
    {
        if (!await _api.GroupDirectoryJoin(new GroupDto(entry.Group)).ConfigureAwait(false))
            throw new InvalidOperationException("Unable to join this syncshell. It may be full, closed, or no longer available.");
    }

    private void DrawBannerImage(string bannerHash)
    {
        if (TryGetDirectoryTexture(bannerHash, out var texture))
        {
            var sourceSize = new Vector2(texture.Width, texture.Height);
            var widthScale = sourceSize.X > 0f ? ImGui.GetContentRegionAvail().X / sourceSize.X : 1f;
            var heightScale = sourceSize.Y > 0f ? 68f * ImGuiHelpers.GlobalScale / sourceSize.Y : 1f;
            ImGui.Image(texture.Handle, sourceSize * Math.Min(1f, Math.Min(widthScale, heightScale)));
            return;
        }

        ImGui.TextColored(_failedDirectoryImages.Contains(bannerHash) ? ImGuiColors.DalamudRed : SnowcloakColours.CompactTextMuted,
            _failedDirectoryImages.Contains(bannerHash) ? "Banner unavailable" : "Loading banner...");
    }

    private void DrawProfilePortrait(string imageHash)
    {
        var size = new Vector2(72f) * ImGuiHelpers.GlobalScale;
        if (TryGetDirectoryTexture(imageHash, out var texture))
        {
            var sourceSize = new Vector2(texture.Width, texture.Height);
            var scale = Math.Min(size.X / Math.Max(1f, sourceSize.X), size.Y / Math.Max(1f, sourceSize.Y));
            ImGui.Image(texture.Handle, sourceSize * scale);
            return;
        }

        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var placeholder = ImRaii.Child("rp-directory-portrait", size, true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                (_failedDirectoryImages.Contains(imageHash) ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.User).ToIconString());
    }

    private bool TryGetDirectoryTexture(string imageHash, out IDalamudTextureWrap texture)
    {
        if (_bannerTextures.TryGetValue(imageHash, out var cachedTexture))
        {
            texture = cachedTexture;
            return true;
        }
        if (_failedDirectoryImages.Contains(imageHash)
            || !_imageTransferService.TryGetImage(imageHash, out var bytes) || bytes.Length == 0)
        {
            texture = null!;
            return false;
        }
        try
        {
            texture = _textureService.LoadImage(bytes);
            _bannerTextures[imageHash] = texture;
            return true;
        }
        catch (Exception ex) when (IsExpectedTextureFailure(ex))
        {
            _failedDirectoryImages.Add(imageHash);
            LogDirectoryImageRenderFailure(_logger, ex);
            texture = null!;
            return false;
        }
    }

    private static bool IsExpectedTextureFailure(Exception exception)
        => exception is AggregateException or InvalidDataException or IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or ObjectDisposedException or InvalidOperationException;

    private static string BuildICalendar(RpEventDirectoryEntryDto entry)
    {
        var shellEvent = entry.Event;
        var builder = new StringBuilder();
        builder.AppendLine("BEGIN:VCALENDAR");
        builder.AppendLine("VERSION:2.0");
        builder.AppendLine("PRODID:-//Snowcloak//Roleplay//EN");
        builder.AppendLine("BEGIN:VEVENT");
        builder.AppendLine("UID:" + shellEvent.Id.ToString("N") + "@snowcloak");
        builder.AppendLine("DTSTART:" + shellEvent.StartsAtUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        if (shellEvent.EndsAtUtc.HasValue) builder.AppendLine("DTEND:" + shellEvent.EndsAtUtc.Value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        builder.AppendLine("SUMMARY:" + EscapeCalendar(shellEvent.Title));
        if (!string.IsNullOrWhiteSpace(shellEvent.Description)) builder.AppendLine("DESCRIPTION:" + EscapeCalendar(shellEvent.Description));
        if (!string.IsNullOrWhiteSpace(shellEvent.Plot)) builder.AppendLine("LOCATION:" + EscapeCalendar(shellEvent.Plot));
        if (shellEvent.Recurrence != null) builder.AppendLine("RRULE:" + BuildRecurrence(shellEvent.Recurrence));
        builder.AppendLine("END:VEVENT");
        builder.AppendLine("END:VCALENDAR");
        return builder.ToString();
    }

    private static string EscapeCalendar(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string BuildRecurrence(GroupEventRecurrenceDto recurrence)
    {
        var value = "FREQ=" + recurrence.Frequency.ToString().ToUpperInvariant() + ";INTERVAL=" + Math.Max(1, recurrence.Interval).ToString(CultureInfo.InvariantCulture);
        if (recurrence.UntilUtc.HasValue) value += ";UNTIL=" + recurrence.UntilUtc.Value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        if (recurrence.OccurrenceCount.HasValue) value += ";COUNT=" + recurrence.OccurrenceCount.Value.ToString(CultureInfo.InvariantCulture);
        if (recurrence.DaysOfWeekMask != 0)
        {
            string[] days = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
            value += ";BYDAY=" + string.Join(',', days.Where((_, index) => (recurrence.DaysOfWeekMask & (1 << index)) != 0));
        }
        return value;
    }

    private void OpenDirectMessage(Pair pair, RpIntroSnapshotDto? intro)
    {
        var key = new ConversationKey(ConversationKind.Direct, pair.UserData.UID);
        if (intro != null)
            _chat.Store.SetDraft(key, intro.Title + Environment.NewLine + intro.Description);
        Mediator.Publish(new OpenChatConversationMessage(key));
    }

    private void DrawBlockedUsers()
    {
        _safety.EnsureLoaded();
        var blocked = _safety.State.BlockedUsers;
        if (blocked.Count == 0)
            return;

        if (ImGui.CollapsingHeader($"Blocked users ({blocked.Count})"))
        {
            foreach (var entry in blocked)
            {
                ImGui.TextUnformatted(entry.User.AliasOrUID);
                ImGui.SameLine();
                using (ImRaii.Disabled(_safety.IsBusy))
                {
                    if (ImGui.SmallButton($"Unblock##rp-{entry.User.UID}"))
                        _safety.Unblock(entry.User.UID);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_safety.Status))
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, _safety.Status);
    }

    private static RpIntroSnapshotDto? CreateIntro(RpCurrentHookDto? hook)
    {
        if (hook == null || string.IsNullOrWhiteSpace(hook.HookId) || string.IsNullOrWhiteSpace(hook.Title)) return null;
        return new RpIntroSnapshotDto
        {
            HookId = hook.HookId.Trim(),
            Title = hook.Title.Trim(),
            Description = hook.Description.Trim(),
        };
    }

    private void OpenReport(ProfileReportSurface surface, string target, long revision = 0)
    {
        _reportSurface = surface;
        _reportTarget = target;
        _reportRevision = revision;
        _reportReason = string.Empty;
        _reportBlockOwner = false;
        _openReportPopup = true;
    }

    private void DrawReportPopup()
    {
        if (_openReportPopup)
        {
            _openReportPopup = false;
            ImGui.OpenPopup("Report RP content");
        }
        if (!ImGui.BeginPopupModal("Report RP content", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextWrapped("Describe the issue for moderators. You can also block the owner from RP discovery and invitations.");
        ImGui.SetNextItemWidth(420f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextMultiline("##rp-report-reason", ref _reportReason, 1000, new Vector2(420f, 90f) * ImGuiHelpers.GlobalScale);
        ImGui.Checkbox("Block the owner", ref _reportBlockOwner);
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_reportReason)))
        {
            if (ImGui.Button("Submit report"))
            {
                Queue(SubmitReportAsync(new RpContentReportDto
                {
                    Surface = _reportSurface,
                    TargetId = _reportTarget,
                    Reason = _reportReason.Trim(),
                    BlockOwner = _reportBlockOwner,
                }), "Report submitted.");
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private async Task SubmitReportAsync(RpContentReportDto report)
    {
        if (report.Surface == ProfileReportSurface.Directory)
        {
            await _api.CharacterProfileReport(new CharacterProfileReportDto(
                report.TargetId, ProfileVisibility.Public, _reportRevision, report.Reason,
                ProfileReportSurface.Directory, report.BlockOwner)).ConfigureAwait(false);
        }
        else
        {
            await _api.RpContentReport(report).ConfigureAwait(false);
        }
        if (report.BlockOwner)
            await _safety.RefreshAsync().ConfigureAwait(false);
    }

    private void StartEligibilityCheck()
        => _profileEligibilityTask = _profiles.GetOwnProfileAsync(ProfileVisibility.Private, forceRefresh: true);

    private bool DrawEligibility()
    {
        if (!_config.Current.PairingSystemEnabled)
        {
            DrawUnavailable("Frostbrand must be enabled before using Roleplay.", "Open Frostbrand",
                () => Mediator.Publish(new OpenFrostbrandUiMessage()));
            return false;
        }

        if (_profileEligibilityTask is not { IsCompleted: true })
        {
            DrawUnavailable("Checking your character profile...", null, null);
            return false;
        }

        if (_profileEligibilityTask.IsFaulted)
        {
            DrawUnavailable("Snowcloak could not check your character profile.", "Retry", StartEligibilityCheck);
            return false;
        }

        var profile = _profileEligibilityTask.GetAwaiter().GetResult();
        if (!profile.IsOwnProfile || profile.Revision <= 0)
        {
            DrawUnavailable("Publish a character profile before using Roleplay.", "Open profile editor",
                () => Mediator.Publish(new UiToggleMessage(typeof(EditProfileUi))), "Retry", StartEligibilityCheck);
            return false;
        }

        return true;
    }

    private static void DrawUnavailable(string message, string? actionLabel, Action? action,
        string? secondaryLabel = null, Action? secondaryAction = null)
    {
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f) * ImGuiHelpers.GlobalScale);
        using var panel = ImRaii.Child("rp-unavailable", new Vector2(-1f, 100f * ImGuiHelpers.GlobalScale), true);
        ModernSection.Header(FontAwesomeIcon.ExclamationTriangle, "Roleplay unavailable");
        ImGui.TextWrapped(message);
        if (actionLabel != null && action != null && ImGui.Button(actionLabel)) action();
        if (secondaryLabel != null && secondaryAction != null)
        {
            ImGui.SameLine();
            if (ImGui.Button(secondaryLabel)) secondaryAction();
        }
    }

    private void LoadAvailabilityDraft()
    {
        var card = _roleplay.OwnAvailability;
        if (card == null) return;
        _availabilityState = card.State;
        _availabilityAudience = card.Audience;
        _availabilityPaused = card.Paused;
        _availabilityTtl = Math.Clamp((int)Math.Ceiling((card.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalMinutes), 5, 1440);
        _availabilityHookId = card.CurrentHook?.HookId;
        _availabilityThemes.Clear();
        _availabilityThemes.UnionWith(card.Themes);
    }

    private void Queue(Task task, string success, Action? after = null)
    {
        _status = string.Empty;
        _ = task.ContinueWith(completed =>
        {
            if (completed.IsFaulted)
                _status = FormatOperationError(completed.Exception);
            else
            {
                _status = success;
                after?.Invoke();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static string FormatOperationError(Exception? exception)
    {
        var error = exception?.GetBaseException();
        if (error == null)
            return "Operation failed.";
        var message = error.Message.Trim();
        const string hubPrefix = "HubException:";
        var hubPrefixIndex = message.LastIndexOf(hubPrefix, StringComparison.Ordinal);
        if (hubPrefixIndex >= 0)
            message = message[(hubPrefixIndex + hubPrefix.Length)..].Trim();
        return string.IsNullOrWhiteSpace(message) ? "Operation failed." : message;
    }

    private static void DrawEmpty(string text, FontAwesomeIcon icon)
    {
        var available = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(24f * ImGuiHelpers.GlobalScale, available.Y * 0.25f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, icon.ToIconString());
        ImGui.SameLine();
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, text);
    }

    private static void DrawRatingFilter(string id, ref ProfileContentRating? rating)
    {
        ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginCombo(id, rating?.ToString() ?? "Any rating")) return;
        if (ImGui.Selectable("Any rating", rating == null)) rating = null;
        foreach (var value in Enum.GetValues<ProfileContentRating>())
            if (ImGui.Selectable(value.ToString(), rating == value)) rating = value;
        ImGui.EndCombo();
    }

    private static void DrawEnumCombo<T>(string id, ref T value, Func<T, string> label, Func<T, bool>? include = null) where T : struct, Enum
    {
        if (!ImGui.BeginCombo(id, label(value))) return;
        foreach (var option in Enum.GetValues<T>())
        {
            if (include != null && !include(option)) continue;
            if (ImGui.Selectable(label(option), EqualityComparer<T>.Default.Equals(option, value))) value = option;
        }
        ImGui.EndCombo();
    }

    private static void DrawChoiceChipRow<T>(string id, IReadOnlyList<T> values, Func<T, string> label,
        Func<T, bool> selected, Action<T> toggle)
    {
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var text = label(value);
            var width = ChoiceChipWidth(text);
            if (index > 0 && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= right)
                ImGui.SameLine();
            if (DrawChoiceChip(id + "-" + index.ToString(CultureInfo.InvariantCulture), text, selected(value)))
                toggle(value);
        }
    }

    private static float ChoiceChipWidth(string label)
        => ImGui.CalcTextSize(label).X + 20f * ImGuiHelpers.GlobalScale;

    private static bool DrawChoiceChip(string id, string label, bool selected)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var textSize = ImGui.CalcTextSize(label);
        var size = new Vector2(ChoiceChipWidth(label), 26f * scale);
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##" + id, size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var fill = selected
            ? new Vector4(SnowcloakColours.OnlineBlue.X, SnowcloakColours.OnlineBlue.Y, SnowcloakColours.OnlineBlue.Z, 0.24f)
            : hovered
                ? new Vector4(0.075f, 0.130f, 0.185f, 0.85f)
                : new Vector4(0.045f, 0.090f, 0.125f, 0.85f);
        var border = selected || hovered ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactBorderSubtle;
        var textColour = selected || hovered ? Vector4.One : SnowcloakColours.CompactTextMuted;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, min + size, Colour.Vector4ToColour(fill), 4f * scale);
        drawList.AddRect(min, min + size, Colour.Vector4ToColour(border), 4f * scale, ImDrawFlags.None, scale);
        drawList.AddText(min + new Vector2(10f * scale, (size.Y - textSize.Y) * 0.5f),
            Colour.Vector4ToColour(textColour), label);
        return clicked;
    }

    private static void ToggleSetValue<T>(ISet<T> values, T value)
    {
        if (!values.Remove(value))
            values.Add(value);
    }

    private static List<string> SplitTerms(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void SearchRooms() => Queue(_roleplay.SearchRoomsAsync(new RoomDirectoryQueryDto
    {
        Search = EmptyToNull(_roomSearch),
        Tags = SplitTerms(_roomTags),
        ContentRating = _roomRating,
        ScenesOnly = _scenesOnly,
        Skip = _roomPage * PageSize,
        Take = PageSize,
    }), "Room directory refreshed.");

    private void SearchEvents() => Queue(_roleplay.SearchEventsAsync(new RpEventDirectoryQueryDto
    {
        Search = EmptyToNull(_eventSearch),
        ThemeTags = SplitTerms(_eventTags),
        ContentRating = _eventRating,
        StartsAfterUtc = DateTime.UtcNow,
        Skip = _eventPage * PageSize,
        Take = PageSize,
    }), "Event directory refreshed.");

    private bool MatchesVenueEventFilters(RoleplayVenueEvent item)
    {
        if (_eventRating is ProfileContentRating.Adult || SplitTerms(_eventTags).Count > 0)
            return false;
        return string.IsNullOrWhiteSpace(_eventSearch)
            || item.Venue.VenueName.Contains(_eventSearch, StringComparison.OrdinalIgnoreCase)
            || (item.Advertisement.Text?.Contains(_eventSearch, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static void DrawPagination(int page, int total, Action<int> setPage)
    {
        if (total <= PageSize)
            return;
        using (ImRaii.Disabled(page <= 0))
            if (ImGui.SmallButton("Previous")) setPage(page - 1);
        ImGui.SameLine();
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            $"Page {page + 1} of {Math.Max(1, (int)Math.Ceiling(total / (double)PageSize))}");
        ImGui.SameLine();
        using (ImRaii.Disabled((page + 1) * PageSize >= total))
            if (ImGui.SmallButton("Next")) setPage(page + 1);
        ImGuiHelpers.ScaledDummy(4f);
    }

    private static string EventKey(RpEventDirectoryEntryDto entry) => entry.Group.GID + ":" + entry.Event.Id.ToString("N");
    private static string FormatEventRecurrence(GroupEventRecurrenceDto recurrence)
    {
        if (recurrence.Interval <= 1)
            return recurrence.Frequency.ToString();
        return recurrence.Frequency switch
        {
            RpEventRecurrenceFrequency.Daily => $"Every {recurrence.Interval} days",
            RpEventRecurrenceFrequency.Weekly => $"Every {recurrence.Interval} weeks",
            RpEventRecurrenceFrequency.Monthly => $"Every {recurrence.Interval} months",
            _ => recurrence.Frequency.ToString(),
        };
    }
    private static string ThemeLabel(RpTheme theme) => theme switch { RpTheme.SliceOfLife => "Slice of life", RpTheme.LoreHeavy => "Lore-heavy", _ => theme.ToString() };
    private static string FormatDuration(int minutes)
    {
        if (minutes < 60)
            return $"{minutes} minutes";
        if (minutes % 60 == 0)
        {
            var hours = minutes / 60;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }
        return $"{minutes / 60} hr {minutes % 60} min";
    }
    private static string AudienceLabel(RpAvailabilityAudience audience) => audience switch { RpAvailabilityAudience.Owner => "Only me", RpAvailabilityAudience.Syncshells => "Syncshells", RpAvailabilityAudience.LocalOnly => "Local only", _ => audience.ToString() };
    private static string AvailabilityStateLabel(RpAvailabilityState state) => state switch { RpAvailabilityState.OpenToWalkUps => "Open to walk-ups", RpAvailabilityState.SeekingHooks => "Seeking hooks", RpAvailabilityState.InScene => "In a scene", RpAvailabilityState.OutOfCharacter => "OOC", RpAvailabilityState.Away => "AFK", _ => "Closed" };
    private static string BoundaryLabel(RpBoundaryRating rating) => rating switch { RpBoundaryRating.AskFirst => "Ask first", RpBoundaryRating.HardNo => "Hard no", _ => "Willing" };
    private static readonly string[] BoundaryKeys =
    [
        "romance", "sexual-themes", "violence", "injury-gore", "horror", "death",
        "captivity-restraint", "power-imbalance", "substance-use", "discrimination",
        "pregnancy-family", "lore-divergence",
    ];
    private static string BoundaryKeyLabel(string key) => key switch
    {
        "sexual-themes" => "Sexual themes",
        "injury-gore" => "Injury or gore",
        "captivity-restraint" => "Captivity or restraint",
        "power-imbalance" => "Power imbalance",
        "substance-use" => "Substance use",
        "pregnancy-family" => "Pregnancy or family",
        "lore-divergence" => "Lore divergence",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(key),
    };
    private static string ProfileTagTypeLabel(ProfileTagType type) => type switch
    {
        ProfileTagType.ChatStyle => "Chat style",
        ProfileTagType.WritingStyle => "Writing style",
        ProfileTagType.LikedCharacter => "Liked character",
        _ => type.ToString(),
    };
}
