using Snowcloak.API.Data;
using Snowcloak.API.Data.Comparer;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.SnowcloakConfiguration;
using Snowcloak.WebAPI;
using System.Collections.Concurrent;

namespace Snowcloak.Services;

public class SnowProfileManager : MediatorSubscriberBase
{
    private const string _noDescription = "-- User has no description set --";
    private const string _nsfw = "Profile not displayed - NSFW";
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly ConcurrentDictionary<UserData, SnowProfileData> _snowProfiles = new(UserDataComparer.Instance);

    private readonly SnowProfileData _defaultProfileData = new(IsFlagged: false, IsNSFW: false, string.Empty, _noDescription);
    private readonly SnowProfileData _loadingProfileData = new(IsFlagged: false, IsNSFW: false, string.Empty, "Loading Data from server...");
    private readonly SnowProfileData _nsfwProfileData = new(IsFlagged: false, IsNSFW: false, string.Empty, _nsfw);

    public SnowProfileManager(ILogger<SnowProfileManager> logger, SnowcloakConfigService snowcloakConfigService,
        SnowMediator mediator, ApiController apiController, ServerConfigurationManager serverConfigurationManager) : base(logger, mediator)
    {
        _snowcloakConfigService = snowcloakConfigService;
        _apiController = apiController;
        _serverConfigurationManager = serverConfigurationManager;

        Mediator.Subscribe<ClearProfileDataMessage>(this, (msg) =>
        {
            if (msg.UserData != null)
                _snowProfiles.Remove(msg.UserData, out _);
            else
                _snowProfiles.Clear();
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => _snowProfiles.Clear());
    }

    public SnowProfileData GetSnowProfile(UserData data)
    {
        if (!_snowProfiles.TryGetValue(data, out var profile))
        {
            _ = Task.Run(() => GetSnowProfileFromService(data));
            return (_loadingProfileData);
        }

        return (profile);
    }

    private async Task GetSnowProfileFromService(UserData data)
    {
        try
        {
            _snowProfiles[data] = _loadingProfileData;
            var profile = await _apiController.UserGetProfile(new API.Dto.User.UserDto(data)).ConfigureAwait(false);
            SnowProfileData profileData = new(profile.Disabled, profile.IsNSFW ?? false,
                string.IsNullOrEmpty(profile.ProfilePictureBase64) ? string.Empty : profile.ProfilePictureBase64,
                string.IsNullOrEmpty(profile.Description) ? _noDescription : profile.Description);
            if (profileData.IsNSFW && !_snowcloakConfigService.Current.ProfilesAllowNsfw && !string.Equals(_apiController.UID, data.UID, StringComparison.Ordinal))
            {
                _snowProfiles[data] = _nsfwProfileData;
            }
            else
            {
                _snowProfiles[data] = profileData;
            }
        }
        catch (Exception ex)
        {
            // if fails save DefaultProfileData to dict
            Logger.LogWarning(ex, "Failed to get Profile from service for user {user}", data);
            _snowProfiles[data] = _defaultProfileData;
        }
    }
}