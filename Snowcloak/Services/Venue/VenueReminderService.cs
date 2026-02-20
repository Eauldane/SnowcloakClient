using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Venue;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.WebAPI;
using System.Globalization;

namespace Snowcloak.Services.Venue;

public sealed class VenueReminderService : IHostedService
{
    private static readonly TimeSpan ReminderWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<VenueReminderService> _logger;
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _configService;
    private readonly IChatGui _chatGui;
    private readonly Lock _syncRoot = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Dictionary<Guid, DateTime> _sentReminderByAd = [];
    private Task? _runTask;

    public VenueReminderService(ILogger<VenueReminderService> logger, ApiController apiController,
        SnowcloakConfigService configService, IChatGui chatGui)
    {
        _logger = logger;
        _apiController = apiController;
        _configService = configService;
        _chatGui = chatGui;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runTask = Task.Run(RunAsync, _cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource.Cancel();
        if (_runTask == null)
            return;

        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    public IReadOnlyList<VenueReminderBookmark> GetBookmarks()
    {
        lock (_syncRoot)
        {
            return [.. GetBookmarkListUnsafe()
                .OrderBy(bookmark => bookmark.Scope)
                .ThenBy(bookmark => bookmark.VenueName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(bookmark => bookmark.CreatedAtUtc)
                .Select(CloneBookmark)];
        }
    }

    public bool IsVenueBookmarked(Guid venueId)
    {
        lock (_syncRoot)
        {
            return GetBookmarkListUnsafe().Any(bookmark =>
                bookmark.Scope == VenueReminderBookmarkScope.Venue && bookmark.VenueId == venueId);
        }
    }

    public bool IsEventBookmarked(Guid adId)
    {
        lock (_syncRoot)
        {
            return GetBookmarkListUnsafe().Any(bookmark =>
                bookmark.Scope == VenueReminderBookmarkScope.Event && bookmark.AdvertisementId == adId);
        }
    }

    public bool AddVenueBookmark(VenueRegistryEntryDto venue)
    {
        if (venue.Id == Guid.Empty)
            return false;

        lock (_syncRoot)
        {
            var bookmarks = GetBookmarkListUnsafe();
            var existing = bookmarks.FirstOrDefault(bookmark =>
                bookmark.Scope == VenueReminderBookmarkScope.Venue && bookmark.VenueId == venue.Id);
            if (existing != null)
            {
                existing.VenueName = NormalizeVenueName(venue.VenueName);
                SaveBookmarksUnsafe();
                return false;
            }

            bookmarks.Add(new VenueReminderBookmark()
            {
                Scope = VenueReminderBookmarkScope.Venue,
                VenueId = venue.Id,
                VenueName = NormalizeVenueName(venue.VenueName)
            });
            SaveBookmarksUnsafe();
            return true;
        }
    }

    public bool AddEventBookmark(VenueRegistryEntryDto venue, VenueAdvertisementDto ad)
    {
        if (venue.Id == Guid.Empty || ad.Id == Guid.Empty)
            return false;

        lock (_syncRoot)
        {
            var bookmarks = GetBookmarkListUnsafe();
            var existing = bookmarks.FirstOrDefault(bookmark =>
                bookmark.Scope == VenueReminderBookmarkScope.Event && bookmark.AdvertisementId == ad.Id);
            if (existing != null)
            {
                existing.VenueId = venue.Id;
                existing.VenueName = NormalizeVenueName(venue.VenueName);
                existing.EventSummary = BuildEventSummary(ad);
                existing.StartsAtUtc = ad.StartsAt;
                SaveBookmarksUnsafe();
                return false;
            }

            bookmarks.Add(new VenueReminderBookmark()
            {
                Scope = VenueReminderBookmarkScope.Event,
                VenueId = venue.Id,
                VenueName = NormalizeVenueName(venue.VenueName),
                AdvertisementId = ad.Id,
                EventSummary = BuildEventSummary(ad),
                StartsAtUtc = ad.StartsAt
            });
            SaveBookmarksUnsafe();
            return true;
        }
    }

    public bool RemoveVenueBookmark(Guid venueId)
    {
        if (venueId == Guid.Empty)
            return false;

        lock (_syncRoot)
        {
            var bookmarks = GetBookmarkListUnsafe();
            var removedCount = bookmarks.RemoveAll(bookmark =>
                bookmark.Scope == VenueReminderBookmarkScope.Venue && bookmark.VenueId == venueId);
            if (removedCount == 0)
                return false;

            SaveBookmarksUnsafe();
            return true;
        }
    }

    public bool RemoveEventBookmark(Guid adId)
    {
        if (adId == Guid.Empty)
            return false;

        lock (_syncRoot)
        {
            var bookmarks = GetBookmarkListUnsafe();
            var removedCount = bookmarks.RemoveAll(bookmark =>
                bookmark.Scope == VenueReminderBookmarkScope.Event && bookmark.AdvertisementId == adId);
            if (removedCount == 0)
                return false;

            SaveBookmarksUnsafe();
            return true;
        }
    }

    public bool RemoveBookmark(Guid bookmarkId)
    {
        if (bookmarkId == Guid.Empty)
            return false;

        lock (_syncRoot)
        {
            var bookmarks = GetBookmarkListUnsafe();
            var removedCount = bookmarks.RemoveAll(bookmark => bookmark.BookmarkId == bookmarkId);
            if (removedCount == 0)
                return false;

            SaveBookmarksUnsafe();
            return true;
        }
    }

    public bool ClearBookmarks()
    {
        lock (_syncRoot)
        {
            var bookmarks = GetBookmarkListUnsafe();
            if (bookmarks.Count == 0)
                return false;

            bookmarks.Clear();
            SaveBookmarksUnsafe();
            return true;
        }
    }

    private async Task RunAsync()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                await CheckForRemindersAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check venue reminders");
            }

            try
            {
                await Task.Delay(PollInterval, _cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckForRemindersAsync(CancellationToken cancellationToken)
    {
        if (!_apiController.IsConnected)
            return;

        List<VenueReminderBookmark> bookmarks;
        lock (_syncRoot)
        {
            bookmarks = [.. GetBookmarkListUnsafe()];
        }

        if (bookmarks.Count == 0)
            return;

        var eventBookmarks = bookmarks
            .Where(bookmark => bookmark.Scope == VenueReminderBookmarkScope.Event && bookmark.AdvertisementId.HasValue)
            .Select(bookmark => bookmark.AdvertisementId!.Value)
            .ToHashSet();
        var venueBookmarks = bookmarks
            .Where(bookmark => bookmark.Scope == VenueReminderBookmarkScope.Venue)
            .Select(bookmark => bookmark.VenueId)
            .ToHashSet();

        if (eventBookmarks.Count == 0 && venueBookmarks.Count == 0)
            return;

        var response = await _apiController.VenueRegistryList(new VenueRegistryListRequestDto(0, 50)
        {
            IncludeAds = true,
            IncludeUnlisted = false
        }).ConfigureAwait(false);

        var nowUtc = DateTime.UtcNow;
        var reminderThresholdUtc = nowUtc.Add(ReminderWindow);
        CleanupSentReminderTracker(nowUtc);

        foreach (var venue in response.Registries)
        {
            if (venue.Advertisements == null)
                continue;

            foreach (var ad in venue.Advertisements)
            {
                if (!ad.IsActive || !ad.StartsAt.HasValue || ad.Id == Guid.Empty)
                    continue;

                var eventBookmarked = eventBookmarks.Contains(ad.Id);
                var venueBookmarked = venueBookmarks.Contains(venue.Id);
                if (!eventBookmarked && !venueBookmarked)
                    continue;

                var startsUtc = DateTime.SpecifyKind(ad.StartsAt.Value, DateTimeKind.Utc);
                if (startsUtc <= nowUtc || startsUtc > reminderThresholdUtc)
                    continue;

                if (!TryTrackReminder(ad.Id, startsUtc))
                    continue;

                PrintReminder(venue, ad, startsUtc, eventBookmarked, venueBookmarked);
            }
        }
    }

    private bool TryTrackReminder(Guid adId, DateTime startUtc)
    {
        lock (_syncRoot)
        {
            if (_sentReminderByAd.TryGetValue(adId, out var existingReminder) && existingReminder == startUtc)
                return false;

            _sentReminderByAd[adId] = startUtc;
            return true;
        }
    }

    private void CleanupSentReminderTracker(DateTime nowUtc)
    {
        lock (_syncRoot)
        {
            var staleKeys = _sentReminderByAd
                .Where(kvp => kvp.Value < nowUtc.AddHours(-2))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var staleKey in staleKeys)
            {
                _sentReminderByAd.Remove(staleKey);
            }
        }
    }

    private void PrintReminder(VenueRegistryEntryDto venue, VenueAdvertisementDto ad, DateTime startsUtc,
        bool eventBookmarked, bool venueBookmarked)
    {
        var venueName = NormalizeVenueName(venue.VenueName);
        var startsLocal = startsUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var bookmarkKind = eventBookmarked
            ? "event bookmark"
            : venueBookmarked
                ? "venue bookmark"
                : "bookmark";
        var summary = BuildEventSummary(ad);

        var message = string.IsNullOrWhiteSpace(summary)
            ? $"[Snowcloak] Reminder ({bookmarkKind}): {venueName} starts at {startsLocal}."
            : $"[Snowcloak] Reminder ({bookmarkKind}): {venueName} starts at {startsLocal}. {summary}";

        _chatGui.Print(new XivChatEntry()
        {
            Message = message,
            Type = XivChatType.SystemMessage
        });
    }

    private List<VenueReminderBookmark> GetBookmarkListUnsafe()
    {
        _configService.Current.VenueReminderBookmarks ??= [];
        return _configService.Current.VenueReminderBookmarks;
    }

    private void SaveBookmarksUnsafe()
    {
        _configService.Save();
    }

    private static string NormalizeVenueName(string? venueName)
    {
        return string.IsNullOrWhiteSpace(venueName) ? "Unnamed Venue" : venueName.Trim();
    }

    private static string BuildEventSummary(VenueAdvertisementDto ad)
    {
        if (string.IsNullOrWhiteSpace(ad.Text))
            return string.Empty;

        var summary = ad.Text
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (summary.Length <= 120)
            return summary;

        return summary[..117] + "...";
    }

    private static VenueReminderBookmark CloneBookmark(VenueReminderBookmark source)
    {
        return new VenueReminderBookmark()
        {
            BookmarkId = source.BookmarkId,
            Scope = source.Scope,
            VenueId = source.VenueId,
            VenueName = source.VenueName,
            AdvertisementId = source.AdvertisementId,
            EventSummary = source.EventSummary,
            StartsAtUtc = source.StartsAtUtc,
            CreatedAtUtc = source.CreatedAtUtc
        };
    }
}
