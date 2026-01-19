using Snowcloak.API.Data;
using Snowcloak.API.Data.Comparer;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Snowcloak.Services;

public class SnowProfileManager : MediatorSubscriberBase
{
    private readonly string _noDescription;
    private readonly string _nsfw;
    private readonly Lazy<ApiController> _apiController;
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly ConcurrentDictionary<ProfileRequestKey, SnowProfileData> _snowProfiles = new(new ProfileRequestKeyComparer());
    private readonly SnowProfileData _defaultProfileData;
    private readonly SnowProfileData _loadingProfileData;
    private readonly SnowProfileData _nsfwProfileData;

    public SnowProfileManager(ILogger<SnowProfileManager> logger, SnowcloakConfigService snowcloakConfigService,
        SnowMediator mediator, IServiceProvider serviceProvider, ServerConfigurationManager serverConfigurationManager) : base(logger, mediator)
    {
        _snowcloakConfigService = snowcloakConfigService;
        _noDescription = "-- User has no description set --";
        _nsfw = "Profile not displayed - The profile is NSFW, but you have this disabled in settings.";
        _defaultProfileData = new(null, false, false, string.Empty, _noDescription, ProfileVisibility.Private);
        _loadingProfileData = new(null, false, false, string.Empty, "Loading Data from server...", ProfileVisibility.Private);
        _nsfwProfileData = new(null, false, false, string.Empty, _nsfw, ProfileVisibility.Private);

        _apiController = new Lazy<ApiController>(() => serviceProvider.GetRequiredService<ApiController>());
        _ = serverConfigurationManager;
        
        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData == null)
            {
                _snowProfiles.Clear();
                return;
            }

            foreach (var key in _snowProfiles.Keys.Where(k => k.User != null && UserDataComparer.Instance.Equals(k.User, msg.UserData)
                                                                             && (msg.Visibility == null || msg.Visibility == k.RequestedVisibility)).ToList())
            {
                _snowProfiles.TryRemove(key, out _);
            }
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => _snowProfiles.Clear());
    }
    
    public SnowProfileData GetSnowProfile(UserData data, ProfileVisibility? visibilityOverride = null)
    {
        return GetSnowProfileInternal(new ProfileRequestKey(data, null, visibilityOverride));
    }

    public SnowProfileData GetSnowProfile(string ident, ProfileVisibility? visibilityOverride = null)
    {
        return GetSnowProfileInternal(new ProfileRequestKey(null, ident, visibilityOverride));
    }

    public Task<SnowProfileData> GetSnowProfileAsync(UserData? userData = null, string? ident = null, ProfileVisibility? visibilityOverride = null, bool forceRefresh = false)
    {
        var key = new ProfileRequestKey(userData, ident, visibilityOverride);
        if (!forceRefresh && _snowProfiles.TryGetValue(key, out var cached) && !ReferenceEquals(cached, _loadingProfileData))
            return Task.FromResult(cached);

        return GetSnowProfileFromService(key);
    }

    private SnowProfileData GetSnowProfileInternal(ProfileRequestKey key)
    {
        if (!_snowProfiles.TryGetValue(key, out var profile))
        {
            var placeholder = _loadingProfileData with { Visibility = key.RequestedVisibility ?? ProfileVisibility.Private, User = key.User ?? new UserData(key.Ident ?? string.Empty) };
            _snowProfiles[key] = placeholder;
            _ = Task.Run(() => GetSnowProfileFromService(key));
            return placeholder;
        }

        return profile;
    }

    private async Task<SnowProfileData> GetSnowProfileFromService(ProfileRequestKey requestKey)
    {
        try
        {
            _snowProfiles[requestKey] = _loadingProfileData;
            var profile = await _apiController.Value.UserGetProfile(new UserProfileRequestDto(requestKey.User, requestKey.Ident, requestKey.RequestedVisibility)).ConfigureAwait(false);
            
            var profileUser = profile.User ?? requestKey.User ?? new UserData(requestKey.Ident ?? string.Empty);
            var visibility = profile.Visibility ?? requestKey.RequestedVisibility ?? ProfileVisibility.Private;
            var profileData = new SnowProfileData(profileUser, profile.Disabled, profile.IsNSFW ?? false,
                string.IsNullOrEmpty(profile.ProfilePictureBase64) ? string.Empty : profile.ProfilePictureBase64,
                string.IsNullOrEmpty(profile.Description) ? _noDescription : profile.Description, visibility);

            if (profileData.IsNSFW && !_snowcloakConfigService.Current.ProfilesAllowNsfw && !string.Equals(_apiController.Value.UID, profileUser?.UID, StringComparison.Ordinal))
            {
                var nsfwData = _nsfwProfileData with { User = profileUser, Visibility = visibility };
                _snowProfiles[requestKey] = nsfwData;
                return nsfwData;
            }
            _snowProfiles[requestKey] = profileData;
            return profileData;
        }
        catch (Exception ex)
        {
            // if fails save DefaultProfileData to dict
            var fallbackUser = requestKey.User ?? new UserData(requestKey.Ident ?? string.Empty);
            Logger.LogWarning(ex, "Failed to get Profile from service for user {user}", fallbackUser);
            var fallbackData = _defaultProfileData with { User = fallbackUser, Visibility = requestKey.RequestedVisibility ?? ProfileVisibility.Private };
            _snowProfiles[requestKey] = fallbackData;
            return fallbackData;
        }
    }

    private readonly record struct ProfileRequestKey(UserData? User, string? Ident, ProfileVisibility? RequestedVisibility);

    private sealed class ProfileRequestKeyComparer : IEqualityComparer<ProfileRequestKey>
    {
        public bool Equals(ProfileRequestKey x, ProfileRequestKey y)
        {
            var usersEqual = x.User != null && y.User != null && UserDataComparer.Instance.Equals(x.User, y.User) || x.User == null && y.User == null;
            return usersEqual
                   && string.Equals(x.Ident, y.Ident, StringComparison.Ordinal)
                   && x.RequestedVisibility == y.RequestedVisibility;
        }

        public int GetHashCode(ProfileRequestKey obj)
        {
            var hash = new HashCode();
            if (obj.User != null) hash.Add(obj.User, UserDataComparer.Instance);
            if (obj.Ident != null) hash.Add(obj.Ident, StringComparer.Ordinal);
            hash.Add(obj.RequestedVisibility);
            return hash.ToHashCode();
        }
    }
}