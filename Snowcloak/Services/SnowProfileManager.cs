using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;

namespace Snowcloak.Services;

public sealed class SnowProfileManager : MediatorSubscriberBase
{
    private readonly Lazy<ApiController> _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SnowcloakConfigService _configService;
    private readonly ConcurrentDictionary<ProfileRequestKey, SnowProfileData> _profiles = new();
    private readonly ConcurrentDictionary<string, CharacterProfileSummaryDto> _summaries = new(StringComparer.Ordinal);
    private string _currentIdent = string.Empty;

    public SnowProfileManager(ILogger<SnowProfileManager> logger, SnowcloakConfigService configService,
        DalamudUtilService dalamudUtilService, SnowMediator mediator, IServiceProvider serviceProvider)
        : base(logger, mediator)
    {
        _configService = configService;
        _dalamudUtilService = dalamudUtilService;
        _apiController = new Lazy<ApiController>(() => serviceProvider.GetRequiredService<ApiController>());

        Mediator.Subscribe<ClearCharacterProfileDataMessage>(this, message =>
        {
            if (string.IsNullOrEmpty(message.Ident))
            {
                _profiles.Clear();
                return;
            }

            foreach (var key in _profiles.Keys
                         .Where(k => string.Equals(k.Ident, message.Ident, StringComparison.Ordinal)
                                     && (message.Visibility == null || message.Visibility == k.Visibility))
                         .ToList())
            {
                _profiles.TryRemove(key, out _);
            }

            if (!message.PreserveSummary && (message.Visibility == null || message.Visibility == ProfileVisibility.Public))
                _summaries.TryRemove(message.Ident, out _);
        });
        Mediator.Subscribe<DisconnectedMessage>(this, _ =>
        {
            _profiles.Clear();
            _summaries.Clear();
            _currentIdent = string.Empty;
        });
    }

    public SnowProfileData GetSnowProfile(Pair pair, ProfileVisibility? visibility = null)
        => GetSnowProfile(pair.Ident, visibility);

    public SnowProfileData GetSnowProfile(string ident, ProfileVisibility? visibility = null)
    {
        if (string.IsNullOrWhiteSpace(ident))
            return Placeholder(string.Empty, visibility ?? ProfileVisibility.Public, "No active character profile is available.");

        var key = new ProfileRequestKey(ident, visibility);
        if (_profiles.TryGetValue(key, out var profile)) return profile;

        profile = Placeholder(ident, visibility ?? ProfileVisibility.Public, "Loading RP profile...");
        _profiles[key] = profile;
        _ = Task.Run(() => RefreshAsync(key));
        return profile;
    }

    public async Task<SnowProfileData> GetSnowProfileAsync(string ident, ProfileVisibility? visibility = null, bool forceRefresh = false)
    {
        var key = new ProfileRequestKey(ident, visibility);
        if (!forceRefresh && _profiles.TryGetValue(key, out var cached)) return cached;
        return await RefreshAsync(key).ConfigureAwait(false);
    }

    public SnowProfileData GetOwnProfile(ProfileVisibility visibility)
    {
        if (!string.IsNullOrEmpty(_currentIdent))
            return GetSnowProfile(_currentIdent, visibility);

        _ = Task.Run(async () =>
        {
            _currentIdent = await _dalamudUtilService.GetPlayerNameHashedAsync().ConfigureAwait(false);
            await RefreshOwnAsync(visibility).ConfigureAwait(false);
        });
        return Placeholder(string.Empty, visibility, "Loading your current character...");
    }

    public async Task<SnowProfileData> GetOwnProfileAsync(ProfileVisibility visibility, bool forceRefresh = false)
    {
        _currentIdent = await _dalamudUtilService.GetPlayerNameHashedAsync().ConfigureAwait(false);
        var key = new ProfileRequestKey(_currentIdent, visibility);
        if (!forceRefresh && _profiles.TryGetValue(key, out var cached)) return cached;
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
            Logger.LogWarning(ex, "Failed to refresh RP profile summary for {ident}", ident);
            ClearSummary(ident);
            return null;
        }
    }

    public void UpdateSummaries(IEnumerable<PairingAvailabilityDto> availability)
    {
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
            Logger.LogWarning(ex, "Failed to get own RP profile for {ident}", _currentIdent);
            return Store(new ProfileRequestKey(_currentIdent, visibility),
                Placeholder(_currentIdent, visibility, "Could not load your RP profile."));
        }
    }

    private async Task<SnowProfileData> RefreshAsync(ProfileRequestKey key)
    {
        try
        {
            var dto = await _apiController.Value.CharacterProfileGet(new CharacterProfileRequestDto(key.Ident, key.Visibility)).ConfigureAwait(false);
            return Store(key, dto);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to get RP profile for {ident}", key.Ident);
            return Store(key, Placeholder(key.Ident, key.Visibility ?? ProfileVisibility.Public, "Could not load this RP profile."));
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
        return profile;
    }

    private SnowProfileData Store(ProfileRequestKey key, SnowProfileData profile)
    {
        _profiles[key] = profile;
        return profile;
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
}
