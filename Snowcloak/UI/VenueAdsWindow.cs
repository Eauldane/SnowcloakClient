using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Venue;
using Snowcloak.API.Routes;
using Snowcloak.Configuration.Models;
using Snowcloak.Services;
using Snowcloak.Services.Housing;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.Venue;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.SignalR.Utils;
using System;
using System.Collections.Concurrent;
using Snowcloak.UI.Components.BbCode;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Snowcloak.UI;

public sealed class VenueAdsWindow : WindowMediatorSubscriberBase
{
    private enum AdsTab
    {
        Browse,
        Manage,
        Reminders
    }

    private const int MaxAdTextLength = 2000;
    private const int BannerWidth = 720;
    private const int BannerHeight = 300;
    private static readonly string[] DayOptions = ["None", "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    
    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly VenueRegistrationService _venueRegistrationService;
    private readonly VenueReminderService _venueReminderService;
    
    private readonly Dictionary<string, IDalamudTextureWrap> _bannerTextures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _bannerBytes = new(StringComparer.Ordinal);

    private readonly List<BrowseAdEntry> _browseAds = [];
    private readonly List<VenueRegistryEntryDto> _ownedVenues = [];
    private int _selectedOwnedVenueIndex = -1;
    private Guid? _selectedAdId;
    private string _adText = string.Empty;
    private string? _bannerBase64;
    private int? _bannerWidth;
    private int? _bannerHeight;
    private int _startDayIndex;
    private string _startTimeText = string.Empty;
    private bool _adIsActive = true;
    private bool _isSaving;
    private bool _isLoadingBrowse;
    private bool _isLoadingOwned;
    private string? _statusMessage;
    private bool _statusIsError;
    private AdsTab? _requestedTab;
    private bool _openCreateOnNextDraw;
    private bool _refreshBrowseOnNextOpen = true;
    private bool _refreshOwnedOnNextOpen = true;

    private sealed record BrowseAdEntry(VenueRegistryEntryDto Venue, VenueAdvertisementDto Advertisement);

    public VenueAdsWindow(ILogger<VenueAdsWindow> logger, SnowMediator mediator, UiSharedService uiSharedService,
        ApiController apiController, FileDialogManager fileDialogManager, DalamudUtilService dalamudUtilService,
        VenueRegistrationService venueRegistrationService, VenueReminderService venueReminderService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Venues###SnowcloakVenueAds", performanceCollectorService)
    {
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _fileDialogManager = fileDialogManager;
        _dalamudUtilService = dalamudUtilService;
        _venueRegistrationService = venueRegistrationService;
        _venueReminderService = venueReminderService;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(650, 500)
        };

        Mediator.Subscribe<OpenVenueAdsWindowMessage>(this, (msg) =>
        {
            _requestedTab = msg.OpenCreate ? AdsTab.Manage : AdsTab.Browse;
            _openCreateOnNextDraw = msg.OpenCreate;
            IsOpen = true;
        });
    }

    public override void OnOpen()
    {
        _refreshBrowseOnNextOpen = true;
        _refreshOwnedOnNextOpen = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var texture in _bannerTextures.Values)
            {
                texture.Dispose();
            }
            _bannerTextures.Clear();
        }
        base.Dispose(disposing);
    }

    protected override void DrawInternal()
    {
        DrawHeader();

        using var tabs = ImRaii.TabBar("VenueAdsTabs");
        var browseFlags = _requestedTab == AdsTab.Browse ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var manageFlags = _requestedTab == AdsTab.Manage ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var bookmarksFlags = _requestedTab == AdsTab.Reminders ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

        using (var browseTab = ImRaii.TabItem("Ads", browseFlags))
        {
            if (browseTab)
            {
                if ((_refreshBrowseOnNextOpen || ImGui.IsItemActivated())
                    && _apiController.ServerState is ServerState.Connected)
                {
                    _refreshBrowseOnNextOpen = false;
                    _ = RefreshBrowseAsync();
                }
                DrawBrowseTab();
            }
        }

        using (var manageTab = ImRaii.TabItem("Venue Management", manageFlags))
        {
            if (manageTab)
            {
                if ((_refreshOwnedOnNextOpen || ImGui.IsItemActivated())
                    && _apiController.ServerState is ServerState.Connected)
                {
                    _refreshOwnedOnNextOpen = false;
                    _ = RefreshOwnedAsync();
                }
                DrawManageTab();
            }
        }
        
        using (var bookmarksTab = ImRaii.TabItem("Event Reminders", bookmarksFlags))
        {
            if (bookmarksTab)
            {
                if (ImGui.IsItemActivated() && _apiController.ServerState is ServerState.Connected)
                {
                    _ = RefreshBrowseAsync();
                }
                DrawBookmarksTab();
            }
        }

        _requestedTab = null;
    }

    private void DrawHeader()
    {
        _uiSharedService.BigText("Venue Advertisements");
        ElezenImgui.ColouredWrappedText("Browse active venue ads or manage your own listings.", ImGuiColors.DalamudGrey);
        ImGui.Separator();

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            var color = _statusIsError ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen;
            ElezenImgui.ColouredWrappedText(_statusMessage, color);
        }
        ImGuiHelpers.ScaledDummy(4);
    }

    private void DrawBrowseTab()
    {
        if (_apiController.ServerState is not ServerState.Connected)
        {
            ElezenImgui.ColouredWrappedText("Connect to Snowcloak to browse venue ads.", ImGuiColors.DalamudRed);
            return;
        }

        if (_isLoadingBrowse)
        {
            ElezenImgui.ColouredWrappedText("Loading ads...", ImGuiColors.DalamudGrey);
            return;
        }

        if (_browseAds.Count == 0)
        {
            ElezenImgui.ColouredWrappedText("No active venue ads were found.", ImGuiColors.DalamudGrey);
            return;
        }

        using var child = ImRaii.Child("VenueAdsBrowse", new Vector2(-1, -1), false);
        foreach (var entry in _browseAds)
        {
            DrawAdCard(entry.Venue, entry.Advertisement);
            ImGui.Separator();
        }
    }
    
    private void DrawBookmarksTab()
    {
        ElezenImgui.ColouredWrappedText(
            "Bookmarks are stored locally. Reminders are sent in chat within one hour before start, "
            + "or immediately if the event is already in progress when you log in.",
            ImGuiColors.DalamudGrey);

        var bookmarks = _venueReminderService.GetBookmarks();
        if (bookmarks.Count == 0)
        {
            ElezenImgui.ColouredWrappedText("You have no venue ad bookmarks yet.", ImGuiColors.DalamudGrey);
            return;
        }

        if (ImGui.Button("Clear all bookmarks"))
        {
            if (_venueReminderService.ClearBookmarks())
            {
                _statusMessage = "All bookmarks were removed.";
                _statusIsError = false;
            }
        }
        UiSharedService.AttachToolTip("Remove every venue reminder bookmark.");
        ImGuiHelpers.ScaledDummy(4);

        var adById = _browseAds
            .Where(entry => entry.Advertisement.Id != Guid.Empty)
            .GroupBy(entry => entry.Advertisement.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var adsByVenue = _browseAds
            .Where(entry => entry.Venue.Id != Guid.Empty)
            .GroupBy(entry => entry.Venue.Id)
            .ToDictionary(group => group.Key,
                group => group
                    .OrderBy(item => item.Advertisement.StartsAt ?? DateTime.MaxValue)
                    .ToList());

        using var child = ImRaii.Child("VenueBookmarksList", new Vector2(-1, -1), false);
        foreach (var bookmark in bookmarks)
        {
            using var id = ImRaii.PushId(bookmark.BookmarkId.ToString("N"));
            var drewLiveAd = false;
            if (bookmark.Scope == VenueReminderBookmarkScope.Event && bookmark.AdvertisementId.HasValue
                && adById.TryGetValue(bookmark.AdvertisementId.Value, out var eventEntry))
            {
                DrawAdCard(eventEntry.Venue, eventEntry.Advertisement, drawReminderControls: false);
                drewLiveAd = true;
            }
            else if (bookmark.Scope == VenueReminderBookmarkScope.Venue
                     && adsByVenue.TryGetValue(bookmark.VenueId, out var venueEntries))
            {
                foreach (var venueEntry in venueEntries)
                {
                    DrawAdCard(venueEntry.Venue, venueEntry.Advertisement, drawReminderControls: false);
                    ImGuiHelpers.ScaledDummy(4);
                }
                drewLiveAd = venueEntries.Count > 0;
            }

            if (!drewLiveAd)
            {
                DrawBookmarkFallback(bookmark);
            }

            if (ImGui.SmallButton("Remove bookmark"))
            {
                if (_venueReminderService.RemoveBookmark(bookmark.BookmarkId))
                {
                    _statusMessage = "Bookmark removed.";
                    _statusIsError = false;
                }
                else
                {
                    _statusMessage = "Failed to remove bookmark.";
                    _statusIsError = true;
                }
            }

            ImGui.Separator();
        }
    }

    private void DrawBookmarkFallback(VenueReminderBookmark bookmark)
    {
        _uiSharedService.BigText(bookmark.VenueName);
        var isEventBookmark = bookmark.Scope == VenueReminderBookmarkScope.Event;
        ElezenImgui.ColouredWrappedText(
            isEventBookmark ? "Reminder scope: This event only" : "Reminder scope: All events from this venue",
            ImGuiColors.DalamudGrey);

        if (isEventBookmark)
        {
            if (!string.IsNullOrWhiteSpace(bookmark.EventSummary))
            {
                ElezenImgui.ColouredWrappedText($"Event: {bookmark.EventSummary}", ImGuiColors.DalamudGrey);
            }

            var startText = bookmark.StartsAtUtc.HasValue
                ? DateTime.SpecifyKind(bookmark.StartsAtUtc.Value, DateTimeKind.Utc).ToLocalTime()
                    .ToString("g", CultureInfo.CurrentCulture)
                : "Unknown";
            ElezenImgui.ColouredWrappedText($"Starts: {startText}", ImGuiColors.DalamudGrey);
        }

        ElezenImgui.ColouredWrappedText("No active ad is currently available for this reminder.", ImGuiColors.DalamudGrey);
    }

    private void DrawAdReminderControls(VenueRegistryEntryDto venue, VenueAdvertisementDto ad)
    {
        if (venue.Id == Guid.Empty || ad.Id == Guid.Empty)
            return;

        using var id = ImRaii.PushId($"reminder_{ad.Id:N}");
        var eventBookmarked = _venueReminderService.IsEventBookmarked(ad.Id);
        var venueBookmarked = _venueReminderService.IsVenueBookmarked(venue.Id);

        using (ImRaii.PushId("event"))
        {
            var eventIcon = eventBookmarked ? FontAwesomeIcon.Times : FontAwesomeIcon.Clock;
            var eventButtonText = eventBookmarked ? "Ignore this event" : "Set a reminder for this event";
            if (ElezenImgui.ShowIconButton(eventIcon, eventButtonText))
            {
                var changed = eventBookmarked
                    ? _venueReminderService.RemoveEventBookmark(ad.Id)
                    : _venueReminderService.AddEventBookmark(venue, ad);
                _statusMessage = changed
                    ? (eventBookmarked ? "Event bookmark removed." : "Event bookmark saved.")
                    : "No changes were made.";
                _statusIsError = false;
            }
        }
        UiSharedService.AttachToolTip(eventBookmarked
            ? "Remove reminder for this event."
            : "Bookmark this event for a reminder in the final hour before start, or while running.");

        ImGui.SameLine();
        using (ImRaii.PushId("venue"))
        {
            var venueIcon = venueBookmarked ? FontAwesomeIcon.Times : FontAwesomeIcon.Users;
            var venueButtonText = venueBookmarked ? "Stop subscribing to this venue" : "Subscribe to this venue";
            if (ElezenImgui.ShowIconButton(venueIcon, venueButtonText))
            {
                var changed = venueBookmarked
                    ? _venueReminderService.RemoveVenueBookmark(venue.Id)
                    : _venueReminderService.AddVenueBookmark(venue);
                _statusMessage = changed
                    ? (venueBookmarked ? "Venue bookmark removed." : "Venue bookmark saved.")
                    : "No changes were made.";
                _statusIsError = false;
            }
        }
        UiSharedService.AttachToolTip(venueBookmarked
            ? "Remove reminder bookmark for this venue."
            : "Bookmark this venue for reminders on all future events.");

        ImGuiHelpers.ScaledDummy(2);
    }

    private void DrawManageTab()
    {
        if (_apiController.ServerState is not ServerState.Connected)
        {
            ElezenImgui.ColouredWrappedText("Connect to Snowcloak to manage venue ads.", ImGuiColors.DalamudRed);
            return;
        }

        if (_isLoadingOwned)
        {
            ElezenImgui.ColouredWrappedText("Loading owned venues...", ImGuiColors.DalamudGrey);
            return;
        }

        _uiSharedService.BigText("Register a venue");
        ElezenImgui.ColouredWrappedText(
            "Stand on the plot you want to register and use the /venue command to open the registration window. "
            + "Make sure you create a syncshell first to associate with your venue!",
            ImGuiColors.DalamudGrey);
        if (_venueRegistrationService.IsRegistrationPending)
        {
            ElezenImgui.ColouredWrappedText(
                "Venue registration is already in progress. Open the placard for your plot to verify ownership.",
                ImGuiColors.DalamudGrey);
        }
        else if (_dalamudUtilService.TryGetLastHousingPlot(out _))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.MapMarkedAlt, "Register current plot"))
            {
                _venueRegistrationService.BeginRegistrationFromCommand();
            }
            UiSharedService.AttachToolTip("Open the placard for your plot to verify ownership.");
        }
        UiSharedService.DistanceSeparator();
        if (_ownedVenues.Count != 0)
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Edit, "Edit venue details"))
            {
                Mediator.Publish(new OpenVenueRegistryWindowMessage());
            }
            UiSharedService.AttachToolTip("Open the venue manager to edit your listing details.");
            UiSharedService.DistanceSeparator();

        }
        if (_ownedVenues.Count == 0)
        {
            ElezenImgui.ColouredWrappedText("No owned venues were found. Register a venue before creating ads.", ImGuiColors.DalamudGrey);
            return;
        }

        if (_openCreateOnNextDraw)
        {
            _openCreateOnNextDraw = false;
            SelectOwnedVenue(_selectedOwnedVenueIndex < 0 ? 0 : _selectedOwnedVenueIndex);
            ResetAdEditor();
        }

        DrawOwnedVenueSelector();
        UiSharedService.DistanceSeparator();
        
        DrawAdEditor();
    }

    private void DrawOwnedVenueSelector()
    {
        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        var label = _selectedOwnedVenueIndex >= 0 && _selectedOwnedVenueIndex < _ownedVenues.Count
            ? _ownedVenues[_selectedOwnedVenueIndex].VenueName ?? "Venue"
            : "Select a venue...";
        if (ImGui.BeginCombo("Owned Venue", label))
        {
            for (var i = 0; i < _ownedVenues.Count; i++)
            {
                var entry = _ownedVenues[i];
                if (ImGui.Selectable(entry.VenueName ?? "Venue", i == _selectedOwnedVenueIndex))
                {
                    SelectOwnedVenue(i);
                }
            }
            ImGui.EndCombo();
        }
    }
    
    private void DrawAdEditor()
    {
        _uiSharedService.BigText("Ad Editor");
        ImGui.TextUnformatted($"Text ({_adText.Length}/{MaxAdTextLength})");
        using (_uiSharedService.GameFont.Push())
        {
            ImGui.InputTextMultiline("##VenueAdText", ref _adText, MaxAdTextLength, ImGuiHelpers.ScaledVector2(-1, 140));
        }

        ImGuiHelpers.ScaledDummy(4);
        ImGui.TextUnformatted("Schedule (Local Time)");
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Day", DayOptions[Math.Clamp(_startDayIndex, 0, DayOptions.Length - 1)]))
        {
            for (var i = 0; i < DayOptions.Length; i++)
            {
                if (ImGui.Selectable(DayOptions[i], i == _startDayIndex))
                {
                    _startDayIndex = i;
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        ImGui.InputText("Start Time (HH:MM)", ref _startTimeText, 5);

        ImGuiHelpers.ScaledDummy(4);
        ImGui.TextUnformatted($"Banner ({BannerWidth}x{BannerHeight})");
        DrawBannerPreview();

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileUpload, "Upload banner"))
        {
            _fileDialogManager.OpenFileDialog("Select banner image", ".png", (success, file) =>
            {
                if (!success)
                    return;

                _ = Task.Run(async () => await HandleBannerSelectionAsync(file).ConfigureAwait(false));
            });
        }
        UiSharedService.AttachToolTip("Upload a 720x300 PNG banner.");

        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Clear banner"))
        {
            ClearBanner();
        }
        UiSharedService.AttachToolTip("Remove the banner from this ad.");

        ImGuiHelpers.ScaledDummy(4);
        ImGui.Checkbox("Ad is active", ref _adIsActive);

        var validationMessage = ValidateAd();
        if (!string.IsNullOrWhiteSpace(validationMessage))
        {
            ElezenImgui.ColouredWrappedText(validationMessage, ImGuiColors.DalamudRed);
        }

        ImGui.BeginDisabled(_isSaving || !string.IsNullOrWhiteSpace(validationMessage));
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, _selectedAdId.HasValue ? "Save Ad" : "Create Ad"))
        {
            _ = SaveAdAsync();
        }
        ImGui.EndDisabled();

        if (_selectedAdId.HasValue)
        {
            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete Ad"))
            {
                _ = DeleteAdAsync();
            }
        }
    }

    private void DrawAdCard(VenueRegistryEntryDto venue, VenueAdvertisementDto ad, bool drawReminderControls = true)
    {
        if (drawReminderControls)
        {
            DrawAdReminderControls(venue, ad);
        }

        var venueName = venue.VenueName ?? "Venue";
        if (!string.IsNullOrWhiteSpace(ad.HexString))
        {
            _uiSharedService.BigText(venueName, ElezenTools.UI.Colour.HexToVector4(ad.HexString));
        }
        else
        {
            _uiSharedService.BigText(venueName);
        }
        if (ImGui.IsItemClicked())
        {
            _ = OpenVenueInfoAsync(ad);
        }
        UiSharedService.AttachToolTip("Open venue details.");

        DrawAdMetadata(ad);

        if (!string.IsNullOrWhiteSpace(ad.BannerBase64))
        {
            DrawBannerImage(ad.BannerBase64, ad.BannerWidth, ad.BannerHeight);
        }

        if (!string.IsNullOrWhiteSpace(ad.Text))
        {
            _uiSharedService.RenderBbCode(ad.Text, ImGui.GetContentRegionAvail().X,
                new BbCodeRenderOptions(AllowImages: false));
        }
    }

    private void DrawAdMetadata(VenueAdvertisementDto ad)
    {
        var hasDateRange = ad.StartsAt.HasValue || ad.EndsAt.HasValue;
        var locationText = BuildPrettyLocationText(ad);
        if (!string.IsNullOrWhiteSpace(locationText))
        {
            ElezenImgui.ColouredWrappedText($"Location: {locationText}", ImGuiColors.DalamudGrey);
        }

        if (hasDateRange || !string.IsNullOrWhiteSpace(locationText))
        {
            if (IsRecentlyStarted(ad))
            {
                ElezenImgui.ColouredWrappedText("When: Now!", ImGuiColors.HealerGreen);
            }
            else
            {
                var start = ad.StartsAt.HasValue
                    ? DateTime.SpecifyKind(ad.StartsAt.Value, DateTimeKind.Utc).ToLocalTime()
                        .ToString("g", CultureInfo.CurrentCulture)
                    : "TBD";
                ElezenImgui.ColouredWrappedText($"When: {start}", ImGuiColors.DalamudGrey);
            }
        }

        if (hasDateRange)
        {
            ImGuiHelpers.ScaledDummy(2);
        }
    }
    
    private string BuildPrettyLocationText(VenueAdvertisementDto ad)
    {
        var locationParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ad.World))
            locationParts.Add(ResolveDisplayName(ad.World, _uiSharedService.WorldData));
        if (!string.IsNullOrWhiteSpace(ad.Territory))
            locationParts.Add(ResolveDisplayName(ad.Territory, _uiSharedService.TerritoryData));
        locationParts.Add(FormatNumericSegment("Ward", ad.Ward));
        if (!string.IsNullOrWhiteSpace(ad.Plot))
            locationParts.Add(FormatNumericSegment("Plot", ad.Plot));
        
        return string.Join(" • ", locationParts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool IsRecentlyStarted(VenueAdvertisementDto ad)
    {
        if (!ad.StartsAt.HasValue)
            return false;

        var startUtc = DateTime.SpecifyKind(ad.StartsAt.Value, DateTimeKind.Utc);
        var nowUtc = DateTime.UtcNow;
        return startUtc <= nowUtc && startUtc >= nowUtc.AddHours(-3);
    }

    private static string ResolveDisplayName(string input, IReadOnlyDictionary<ushort, string> data)
    {
        var trimmed = input.Trim();
        if (ushort.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)
            && data.TryGetValue(numeric, out var numericMatch))
        {
            return numericMatch;
        }
        var match = data.FirstOrDefault(kvp => string.Equals(kvp.Value, trimmed, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match.Value))
            return match.Value;

        return trimmed;
    }

    private static string ResolveDisplayName(string input, IReadOnlyDictionary<uint, string> data)
    {
        var trimmed = input.Trim();
        if (uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)
            && data.TryGetValue(numeric, out var numericMatch))
        {
            return numericMatch;
        }
        var match = data.FirstOrDefault(kvp => string.Equals(kvp.Value, trimmed, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match.Value))
            return match.Value;

        return trimmed;
    }

    private static string FormatNumericSegment(string label, string value)
    {
        if (TryResolveNumeric(value, out var numeric))
            return $"{label} {numeric}";

        return $"{label} {value.Trim()}";
    }

    private async Task OpenVenueInfoAsync(VenueAdvertisementDto ad)
    {
        if (!_apiController.IsConnected)
            return;

        if (!TryResolveAdLocation(ad, out var location, out var errorMessage))
        {
            _statusMessage = errorMessage ?? "Venue location could not be resolved.";
            _statusIsError = true;
            return;
        }

        try
        {
            var response = await _apiController.VenueGetInfoForPlot(new VenueInfoRequestDto(
                new VenueLocationDto(location.WorldId, location.TerritoryId, location.DivisionId, location.WardId, location.PlotId,
                    location.RoomId, location.IsApartment))).ConfigureAwait(false);
            if (response?.HasVenue != true || response.Venue == null)
            {
                _statusMessage = "No venue was found for that location.";
                _statusIsError = true;
                return;
            }

            var prompt = new VenueSyncshellPrompt(response.Venue, location);
            Mediator.Publish(new OpenVenueSyncshellPopupMessage(prompt));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load venue info");
            _statusMessage = "Failed to load venue info.";
            _statusIsError = true;
        }
    }

    private bool TryResolveAdLocation(VenueAdvertisementDto ad, out HousingPlotLocation location, out string? errorMessage)
    {
        location = default;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(ad.World) || string.IsNullOrWhiteSpace(ad.Territory)
            || string.IsNullOrWhiteSpace(ad.Ward) || string.IsNullOrWhiteSpace(ad.Plot))
        {
            errorMessage = "Venue location is incomplete for this ad.";
            return false;
        }

        if (!TryResolveWorldId(ad.World, out var worldId)
            || !TryResolveTerritoryId(ad.Territory, out var territoryId)
            || !TryResolveNumeric(ad.Ward, out var wardId)
            || !TryResolveNumeric(ad.Plot, out var plotId))
        {
            errorMessage = "Venue location could not be resolved for this ad.";
            return false;
        }

        location = new HousingPlotLocation(worldId, territoryId, 0, wardId, plotId, 0, false);
        return true;
    }

    private bool TryResolveWorldId(string worldName, out uint worldId)
    {
        worldId = 0;
        var trimmed = worldName.Trim();
        if (ushort.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var numericWorld))
        {
            worldId = numericWorld;
            return worldId > 0;
        }

        var match = _uiSharedService.WorldData.FirstOrDefault(kvp =>
            string.Equals(kvp.Value, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match.Key == 0 && !string.Equals(match.Value, trimmed, StringComparison.OrdinalIgnoreCase))
            return false;

        worldId = match.Key;
        return worldId > 0;
    }

    private bool TryResolveTerritoryId(string territoryName, out uint territoryId)
    {
        territoryId = 0;
        var match = _uiSharedService.TerritoryData.FirstOrDefault(kvp =>
            string.Equals(kvp.Value, territoryName, StringComparison.OrdinalIgnoreCase));
        if (match.Key == 0 && !string.Equals(match.Value, territoryName, StringComparison.OrdinalIgnoreCase))
            return false;

        territoryId = match.Key;
        return territoryId > 0;
    }

    private static bool TryResolveNumeric(string value, out uint result)
    {
        result = 0;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private void DrawBannerPreview()
    {
        if (!string.IsNullOrWhiteSpace(_bannerBase64))
        {
            DrawBannerImage(_bannerBase64!, _bannerWidth, _bannerHeight);
            return;
        }

        ElezenImgui.ColouredWrappedText("No banner selected.", ImGuiColors.DalamudGrey);
    }

    private void DrawBannerImage(string bannerBase64, int? width, int? height)
    {
        if (_bannerTextures.TryGetValue(bannerBase64, out var texture))
        {
            var availableWidth = Math.Max(0f, ImGui.GetContentRegionAvail().X);
            var targetWidth = width ?? BannerWidth;
            var targetHeight = height ?? BannerHeight;
            var scale = availableWidth > 0 ? Math.Min(1f, availableWidth / targetWidth) : 1f;
            var size = new Vector2(targetWidth * scale, targetHeight * scale);
            ImGui.Image(texture.Handle, size);
            return;
        }

        if (!_bannerBytes.TryGetValue(bannerBase64, out var bytes))
        {
            try
            {
                bytes = Convert.FromBase64String(bannerBase64);
                _bannerBytes[bannerBase64] = bytes;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decode banner image");
                ElezenImgui.ColouredWrappedText("Failed to decode banner image.", ImGuiColors.DalamudRed);
                return;
            }
        }

        if (bytes.Length > 0)
        {
            try
            {
                var tex = _uiSharedService.LoadImage(bytes);
                _bannerTextures[bannerBase64] = tex;
                _bannerBytes.TryRemove(bannerBase64, out _);
                DrawBannerImage(bannerBase64, width, height);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load banner image");
                ElezenImgui.ColouredWrappedText("Failed to load banner image.", ImGuiColors.DalamudRed);
            }
            return;
        }

        ElezenImgui.ColouredWrappedText("Loading banner...", ImGuiColors.DalamudGrey);
    }

    private void SelectOwnedVenue(int index)
    {
        if (index < 0 || index >= _ownedVenues.Count)
            return;

        _selectedOwnedVenueIndex = index;
        var venue = _ownedVenues[index];
        var ad = venue.Advertisements?.FirstOrDefault();
        if (ad != null)
        {
            LoadAdForEdit(ad);
        }
        else
        {
            ResetAdEditor();
        }
    }

    private void ResetAdEditor()
    {
        _selectedAdId = null;
        _adText = string.Empty;
        _bannerBase64 = null;
        _bannerWidth = null;
        _bannerHeight = null;
        _startDayIndex = 0;
        _startTimeText = string.Empty;
        _adIsActive = true;
        _statusMessage = null;
    }

    private void LoadAdForEdit(VenueAdvertisementDto ad)
    {
        _selectedAdId = ad.Id;
        _adText = ad.Text ?? string.Empty;
        _bannerBase64 = ad.BannerBase64;
        _bannerWidth = ad.BannerWidth;
        _bannerHeight = ad.BannerHeight;
        if (ad.StartsAt.HasValue)
        {
            var localStart = DateTime.SpecifyKind(ad.StartsAt.Value, DateTimeKind.Utc).ToLocalTime();
            _startDayIndex = (int)localStart.DayOfWeek + 1;
            _startTimeText = localStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        else
        {
            _startDayIndex = 0;
            _startTimeText = string.Empty;
        }
        _adIsActive = ad.IsActive;
        _statusMessage = null;
    }

    private string? ValidateAd()
    {
        var text = _adText.Trim();
        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasBanner = !string.IsNullOrWhiteSpace(_bannerBase64);

        if (!hasText && !hasBanner)
            return "Ads must include text or a banner image.";

        if (_adText.Length > MaxAdTextLength)
            return "Ad text is too long.";

        if (hasBanner && (_bannerWidth != BannerWidth || _bannerHeight != BannerHeight))
            return $"Banner must be exactly {BannerWidth}x{BannerHeight}.";

        if (!TryResolveStartDateTimeLocal(out _, out var dateError))
            return dateError;

        return null;
    }

    private bool TryResolveStartDateTimeLocal(out DateTime? startLocal, out string? errorMessage)
    {
        startLocal = null;
        errorMessage = null;

        var hasDay = _startDayIndex > 0;
        var hasTime = !string.IsNullOrWhiteSpace(_startTimeText);
        if (!hasDay && !hasTime)
            return true;

        if (!hasDay || !hasTime)
        {
            errorMessage = "Provide both a day and start time or leave both empty.";
            return false;
        }
        
        if (!DateTime.TryParseExact(_startTimeText.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timePart))
        {
            errorMessage = "Start time must be in 24h HH:MM format.";
            return false;
        }

        var selectedDay = (DayOfWeek)(_startDayIndex - 1);
        var now = DateTime.Now;
        var today = now.Date;
        var dayOffset = ((int)selectedDay - (int)today.DayOfWeek + 7) % 7;
        var candidate = today.AddDays(dayOffset).Add(new TimeSpan(timePart.Hour, timePart.Minute, 0));
        if (candidate < now)
        {
            candidate = candidate.AddDays(7);
        }
        startLocal = DateTime.SpecifyKind(candidate, DateTimeKind.Local);
        return true;
    }

    private async Task HandleBannerSelectionAsync(string filePath)
    {
        try
        {
            var fileBytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            using var ms = new MemoryStream(fileBytes);
            var dimensions = PngHdr.TryExtractDimensions(ms);
            if (dimensions.Width != BannerWidth || dimensions.Height != BannerHeight)
            {
                _statusMessage = $"Banner must be exactly {BannerWidth}x{BannerHeight}.";
                _statusIsError = true;
                return;
            }

            var extension = Path.GetExtension(filePath);
            if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                _statusMessage = "Banner image must be a PNG file.";
                _statusIsError = true;
                return;
            }

            _bannerBase64 = Convert.ToBase64String(fileBytes);
            _bannerWidth = BannerWidth;
            _bannerHeight = BannerHeight;
            _statusMessage = "Banner loaded. Remember to save the ad.";
            _statusIsError = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load banner image");
            _statusMessage = "Failed to load banner image.";
            _statusIsError = true;
        }
    }

    private void ClearBanner()
    {
        _bannerBase64 = null;
        _bannerWidth = null;
        _bannerHeight = null;
    }

    private async Task RefreshBrowseAsync()
    {
        if (_isLoadingBrowse)
            return;

        _isLoadingBrowse = true;
        try
        {
            var response = await _apiController.VenueRegistryList(new VenueRegistryListRequestDto(0, 50)
            {
                IncludeAds = true,
                IncludeUnlisted = false
            }).ConfigureAwait(false);
            _browseAds.Clear();
            var activeAds = response.Registries
                .SelectMany(venue => venue.Advertisements
                    .Where(ad => ad.IsActive)
                    .Select(ad => new BrowseAdEntry(venue, ad)))
                .ToList();
            var playerRegion = await GetPlayerRegionAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(playerRegion))
            {
                activeAds = activeAds
                    .Where(entry => TryGetAdRegion(entry.Advertisement, out var adRegion)
                                    && IsRegionVisible(playerRegion, adRegion))
                    .ToList();
            }
            var orderedAds = activeAds
                .OrderByDescending(entry => IsRecentlyStarted(entry.Advertisement))
                .ThenByDescending(entry => IsRecentlyStarted(entry.Advertisement)
                    ? entry.Advertisement.StartsAt
                    : DateTime.MinValue)
                .ThenBy(entry => entry.Advertisement.StartsAt ?? DateTime.MaxValue)
                .ToList();
            _browseAds.AddRange(orderedAds);
            
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load venue ads");
            _statusMessage = "Failed to load venue ads.";
            _statusIsError = true;
        }
        finally
        {
            _isLoadingBrowse = false;
        }
    }

    private async Task<string> GetPlayerRegionAsync()
    {
        var worldId = await _dalamudUtilService.GetHomeWorldIdAsync().ConfigureAwait(false);
        if (worldId == 0 || worldId > ushort.MaxValue)
            return string.Empty;

        return _dalamudUtilService.TryGetWorldRegion((ushort)worldId, out var region)
            ? region
            : string.Empty;
    }

    private bool TryGetAdRegion(VenueAdvertisementDto ad, out string region)
    {
        region = string.Empty;
        if (string.IsNullOrWhiteSpace(ad.World))
            return false;

        if (!TryResolveWorldId(ad.World, out var worldId))
            return false;

        if (worldId == 0 || worldId > ushort.MaxValue)
            return false;

        return _dalamudUtilService.TryGetWorldRegion((ushort)worldId, out region);
    }

    private static bool IsRegionVisible(string playerRegion, string adRegion)
    {
        if (string.Equals(playerRegion, adRegion, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.Equals(playerRegion, "Oceania", StringComparison.OrdinalIgnoreCase)
               && string.Equals(adRegion, "Oceania", StringComparison.OrdinalIgnoreCase);
    }
    
    private async Task RefreshOwnedAsync()
    {
        if (_isLoadingOwned)
            return;

        _isLoadingOwned = true;
        try
        {
            var response = await _apiController.VenueRegistryListOwned(new VenueRegistryListOwnedRequestDto(0, 50)
            {
                IncludeAds = true,
                IncludeUnlisted = true
            }).ConfigureAwait(false);
            _ownedVenues.Clear();
            _ownedVenues.AddRange(response.Registries);
            if (_selectedOwnedVenueIndex < 0 && _ownedVenues.Count > 0)
            {
                SelectOwnedVenue(0);
            }
            else if (_selectedOwnedVenueIndex >= 0 && _selectedOwnedVenueIndex < _ownedVenues.Count)
            {
                SelectOwnedVenue(_selectedOwnedVenueIndex);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load owned venues");
            _statusMessage = "Failed to load owned venues.";
            _statusIsError = true;
        }
        finally
        {
            _isLoadingOwned = false;
        }
    }

    private async Task SaveAdAsync()
    {
        if (_selectedOwnedVenueIndex < 0 || _selectedOwnedVenueIndex >= _ownedVenues.Count)
            return;

        var validation = ValidateAd();
        if (!string.IsNullOrWhiteSpace(validation))
        {
            _statusMessage = validation;
            _statusIsError = true;
            return;
        }

        _isSaving = true;
        _statusMessage = null;
        _statusIsError = false;

        try
        {
            var venue = _ownedVenues[_selectedOwnedVenueIndex];
            if (!TryResolveStartDateTimeLocal(out var startLocal, out var dateError))
            {
                _statusMessage = dateError;
                _statusIsError = true;
                return;
            }
            var request = new VenueAdvertisementUpsertRequestDto(venue.Id, _selectedAdId)
            {
                Text = string.IsNullOrWhiteSpace(_adText) ? null : _adText.Trim(),
                BannerBase64 = _bannerBase64,
                StartsAt = startLocal?.ToUniversalTime(),
                IsActive = _adIsActive
            };

            var response = await _apiController.VenueAdvertisementUpsert(request).ConfigureAwait(false);
            if (!response.Success)
            {
                _statusMessage = response.ErrorMessage ?? "Failed to save ad.";
                _statusIsError = true;
                return;
            }

            _statusMessage = response.WasUpdate ? "Ad updated successfully." : "Ad created successfully.";
            _statusIsError = false;

            await RefreshOwnedAsync().ConfigureAwait(false);
            if (response.Advertisement != null)
            {
                _selectedAdId = response.Advertisement.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save venue ad");
            _statusMessage = "Failed to save venue ad.";
            _statusIsError = true;
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task DeleteAdAsync()
    {
        if (_selectedOwnedVenueIndex < 0 || _selectedOwnedVenueIndex >= _ownedVenues.Count || !_selectedAdId.HasValue)
            return;

        _isSaving = true;
        _statusMessage = null;
        _statusIsError = false;
        try
        {
            var venue = _ownedVenues[_selectedOwnedVenueIndex];
            var request = new VenueAdvertisementDeleteRequestDto(venue.Id, _selectedAdId.Value);
            var response = await _apiController.VenueAdvertisementDelete(request).ConfigureAwait(false);
            if (!response.Success)
            {
                _statusMessage = response.ErrorMessage ?? "Failed to delete ad.";
                _statusIsError = true;
                return;
            }

            _statusMessage = "Ad deleted.";
            _statusIsError = false;
            await RefreshOwnedAsync().ConfigureAwait(false);
            ResetAdEditor();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete venue ad");
            _statusMessage = "Failed to delete venue ad.";
            _statusIsError = true;
        }
        finally
        {
            _isSaving = false;
        }
    }
}
