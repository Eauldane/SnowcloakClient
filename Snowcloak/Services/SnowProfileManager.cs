using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI;

namespace Snowcloak.Services;

public sealed partial class SnowProfileManager : DisposableMediatorSubscriberBase, IAsyncDisposable
{
    private static readonly TimeSpan ErrorProfileRetryAfter = TimeSpan.FromSeconds(30);

    private readonly Lazy<ApiController> _apiController;
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly ConcurrentDictionary<ProfileRequestKey, SnowProfileData> _profiles = new();
    private readonly ConcurrentDictionary<ProfileRequestKey, DateTime> _profileErrorTimes = new();
    private readonly ConcurrentDictionary<string, CharacterProfileSummaryDto> _summaries = new(StringComparer.Ordinal);
    private int _disposed;
    private string _currentIdent = string.Empty;

    public SnowProfileManager(ILogger<SnowProfileManager> logger, SnowcloakConfigService configService,
        DalamudUtilService dalamudUtilService, SnowMediator mediator, IServiceProvider serviceProvider)
        : base(logger, mediator)
    {
        _configService = configService;
        _dalamudUtilService = dalamudUtilService;
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _apiController = new Lazy<ApiController>(() => serviceProvider.GetRequiredService<ApiController>());

        Mediator.Subscribe<ClearCharacterProfileDataMessage>(this, message =>
        {
            if (string.IsNullOrEmpty(message.Ident))
            {
                _profiles.Clear();
                _profileErrorTimes.Clear();
                return;
            }

            foreach (var key in _profiles.Keys
                         .Where(k => string.Equals(k.Ident, message.Ident, StringComparison.Ordinal)
                                     && (message.Visibility == null || message.Visibility == k.Visibility))
                         .ToList())
            {
                _profiles.TryRemove(key, out _);
                _profileErrorTimes.TryRemove(key, out _);
            }

            if (!message.PreserveSummary && (message.Visibility == null || message.Visibility == ProfileVisibility.Public))
                _summaries.TryRemove(message.Ident, out _);
        });
        Mediator.Subscribe<DisconnectedMessage>(this, _ =>
        {
            _profiles.Clear();
            _profileErrorTimes.Clear();
            _summaries.Clear();
            _currentIdent = string.Empty;
        });
    }

    public SnowProfileData GetSnowProfile(Pair pair, ProfileVisibility? visibility = null)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return GetSnowProfile(pair.Ident, visibility);
    }

    public SnowProfileData GetSnowProfile(string ident, ProfileVisibility? visibility = null)
    {
        if (string.IsNullOrWhiteSpace(ident))
            return Placeholder(string.Empty, visibility ?? ProfileVisibility.Public, "No active character profile is available.");

        var key = new ProfileRequestKey(ident, visibility);
        if (TryGetCachedProfile(key, out var profile)) return profile;

        profile = Placeholder(ident, visibility ?? ProfileVisibility.Public, "Loading RP profile...");
        _profiles[key] = profile;
        _ = _backgroundTasks.Run(ct => RefreshAsync(key, ct), nameof(RefreshAsync), _runtimeCts.Token);
        return profile;
    }

    public async Task<SnowProfileData> GetSnowProfileAsync(string ident, ProfileVisibility? visibility = null, bool forceRefresh = false)
    {
        var key = new ProfileRequestKey(ident, visibility);
        if (!forceRefresh && TryGetCachedProfile(key, out var cached)) return cached;
        return await RefreshAsync(key, _runtimeCts.Token).ConfigureAwait(false);
    }

    public SnowProfileData GetOwnProfile(ProfileVisibility visibility)
    {
        if (!string.IsNullOrEmpty(_currentIdent))
            return GetSnowProfile(_currentIdent, visibility);

        _ = _backgroundTasks.Run(async ct =>
        {
            ct.ThrowIfCancellationRequested();
            _currentIdent = await _dalamudUtilService.GetPlayerNameHashedAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await RefreshOwnAsync(visibility).ConfigureAwait(false);
        }, nameof(GetOwnProfile), _runtimeCts.Token);
        return Placeholder(string.Empty, visibility, "Loading your current character...");
    }

    public async Task<SnowProfileData> GetOwnProfileAsync(ProfileVisibility visibility, bool forceRefresh = false)
    {
        _currentIdent = await _dalamudUtilService.GetPlayerNameHashedAsync().ConfigureAwait(false);
        var key = new ProfileRequestKey(_currentIdent, visibility);
        if (!forceRefresh && TryGetCachedProfile(key, out var cached)) return cached;
        return await RefreshOwnAsync(visibility).ConfigureAwait(false);
    }

    public CharacterProfileSummaryDto? GetSummary(string ident)
        => _summaries.TryGetValue(ident, out var summary) ? summary : null;

    public async Task<CharacterProfileSummaryDto?> RefreshSummaryAsync(string ident)
    {
        if (string.IsNullOrWhiteSpace(ident))
            return null;

        try
        {
            var dto = await _apiController.Value.CharacterProfileGet(new CharacterProfileRequestDto(ident, ProfileVisibility.Public)).ConfigureAwait(false);
            var profile = Store(new ProfileRequestKey(dto.Ident, ProfileVisibility.Public), dto);
            if (profile.Revision <= 0 || profile.Disabled)
            {
                ClearSummary(ident);
                return null;
            }

            var summary = ToSummary(profile);
            UpdateSummary(summary);
            return GetSummary(summary.Ident);
        }
        catch (Exception ex)
        {
            LogProfileSummaryRefreshFailed(Logger, ex, ident);
            ClearSummary(ident);
            return null;
        }
    }

    public void UpdateSummaries(IEnumerable<PairingAvailabilityDto> availability)
    {
        ArgumentNullException.ThrowIfNull(availability);

        foreach (var entry in availability)
        {
            if (entry.Profile != null)
                UpdateSummary(entry.Profile);
            else
                ClearSummary(entry.Ident);
        }
    }

    public void UpdateSummary(CharacterProfileSummaryDto summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!string.IsNullOrWhiteSpace(summary.Ident))
            _summaries[summary.Ident] = MaskAdultSummary(summary);
    }

    public void ClearSummary(string ident) => _summaries.TryRemove(ident, out _);

    private async Task<SnowProfileData> RefreshOwnAsync(ProfileVisibility visibility)
    {
        try
        {
            var dto = await _apiController.Value.CharacterProfileGetOwn(visibility).ConfigureAwait(false);
            return Store(new ProfileRequestKey(dto.Ident, visibility), dto);
        }
        catch (Exception ex)
        {
            LogOwnProfileRefreshFailed(Logger, ex, _currentIdent);
            return Store(new ProfileRequestKey(_currentIdent, visibility),
                Placeholder(_currentIdent, visibility, "Could not load your RP profile."), cacheFailure: true);
        }
    }

    private async Task<SnowProfileData> RefreshAsync(ProfileRequestKey key, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dto = await _apiController.Value.CharacterProfileGet(new CharacterProfileRequestDto(key.Ident, key.Visibility)).ConfigureAwait(false);
            return Store(key, dto);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Store(key, Placeholder(key.Ident, key.Visibility ?? ProfileVisibility.Public, "Profile request cancelled."));
        }
        catch (Exception ex)
        {
            LogProfileRefreshFailed(Logger, ex, key.Ident);
            return Store(key, Placeholder(key.Ident, key.Visibility ?? ProfileVisibility.Public, "Could not load this RP profile."), cacheFailure: true);
        }
    }

    private SnowProfileData Store(ProfileRequestKey key, CharacterProfileDto dto)
    {
        var document = dto.Document ?? new();
        if (document.ContentRating == ProfileContentRating.Adult
            && !_configService.Current.ProfilesAllowNsfw
            && !dto.IsOwnProfile)
        {
            document = new CharacterProfileDocumentDto
            {
                CharacterName = "Adult RP profile",
                Tagline = "Adult RP profile hidden by your profile-content settings.",
                ContentRating = document.ContentRating,
            };
        }

        var profile = new SnowProfileData(
            dto.Ident,
            dto.User,
            dto.Visibility,
            dto.Revision,
            dto.Disabled,
            dto.DisabledReason,
            dto.IsOwnProfile,
            dto.UpdatedAtUtc,
            document);
        _profiles[key] = profile;
        _profileErrorTimes.TryRemove(key, out _);
        return profile;
    }

    private SnowProfileData Store(ProfileRequestKey key, SnowProfileData profile, bool cacheFailure = false)
    {
        _profiles[key] = profile;
        if (cacheFailure)
            _profileErrorTimes[key] = DateTime.UtcNow;
        else
            _profileErrorTimes.TryRemove(key, out _);
        return profile;
    }

    private bool TryGetCachedProfile(ProfileRequestKey key, out SnowProfileData profile)
    {
        if (!_profiles.TryGetValue(key, out var cached))
        {
            profile = null!;
            return false;
        }

        profile = cached;
        if (!_profileErrorTimes.TryGetValue(key, out var failedAt))
            return true;

        if (failedAt.Add(ErrorProfileRetryAfter) > DateTime.UtcNow)
            return true;

        _profileErrorTimes.TryRemove(key, out _);
        _profiles.TryRemove(key, out _);
        profile = null!;
        return false;
    }

    private CharacterProfileSummaryDto MaskAdultSummary(CharacterProfileSummaryDto summary)
    {
        if (summary.ContentRating != ProfileContentRating.Adult || _configService.Current.ProfilesAllowNsfw)
            return summary;

        return summary with
        {
            CharacterName = "Adult RP profile",
            Title = string.Empty,
            Pronouns = string.Empty,
            Tagline = "Adult RP profile hidden by your profile-content settings.",
            RpStatus = string.Empty,
            Approachability = string.Empty,
            Hooks = [],
            Tags = [],
        };
    }

    private static CharacterProfileSummaryDto ToSummary(SnowProfileData profile)
        => new()
        {
            Ident = profile.Ident,
            CharacterName = profile.Document.CharacterName,
            Title = profile.Document.Title,
            Pronouns = profile.Document.Pronouns,
            Tagline = profile.Document.Tagline,
            RpStatus = profile.Document.RpStatus,
            Approachability = profile.Document.Approachability,
            Hooks = profile.Document.Hooks,
            ContentRating = profile.Document.ContentRating,
            Tags = profile.Document.Tags,
        };

    private static SnowProfileData Placeholder(string ident, ProfileVisibility visibility, string reason)
        => new(ident, null, visibility, 0, false, reason, false, null, new CharacterProfileDocumentDto());

    private readonly record struct ProfileRequestKey(string Ident, ProfileVisibility? Visibility);

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Failed to get own RP profile for {Ident}")]
    private static partial void LogOwnProfileRefreshFailed(ILogger logger, Exception exception, string ident);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to get RP profile for {Ident}")]
    private static partial void LogProfileRefreshFailed(ILogger logger, Exception exception, string ident);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to refresh RP profile summary for {Ident}")]
    private static partial void LogProfileSummaryRefreshFailed(ILogger logger, Exception exception, string ident);

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing);
        _runtimeCts.Cancel();
        _backgroundTasks.StopAccepting();
        _backgroundTasks.StopSynchronously(Logger, TimeSpan.FromSeconds(2), nameof(SnowProfileManager));
        _runtimeCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        base.Dispose(disposing: true);
        await _runtimeCts.CancelAsync().ConfigureAwait(false);
        await _backgroundTasks.StopAsync().ConfigureAwait(false);
        _runtimeCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
