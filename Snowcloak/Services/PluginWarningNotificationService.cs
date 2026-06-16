using Snowcloak.API.Data;
using Snowcloak.API.Data.Comparer;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Data;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using System.Collections.Concurrent;
using System.Globalization;

namespace Snowcloak.Services;

public class PluginWarningNotificationService : IMediatorSubscriber
{
    private readonly ConcurrentDictionary<UserData, OptionalPluginWarning> _cachedOptionalPluginWarnings = new(UserDataComparer.Instance);
    private readonly IpcManager _ipcManager;
    private readonly SnowcloakConfigService _snowcloakConfigService;

    public SnowMediator Mediator { get; }
    
    public PluginWarningNotificationService(SnowcloakConfigService snowcloakConfigService, IpcManager ipcManager, SnowMediator mediator)
    {
        _snowcloakConfigService = snowcloakConfigService;
        _ipcManager = ipcManager;
        Mediator = mediator;
        Mediator.Subscribe<ClearProfileDataMessage>(this, message => ClearWarning(message.UserData));
        Mediator.Subscribe<DisconnectedMessage>(this, _ => _cachedOptionalPluginWarnings.Clear());
    }
    
    public void NotifyForMissingPlugins(UserData user, string playerName, IReadOnlyCollection<PlayerChanges> changes)
    {
        if (!_cachedOptionalPluginWarnings.TryGetValue(user, out var warning))
        {
            _cachedOptionalPluginWarnings[user] = warning = new()
            {
                ShownCustomizePlusWarning = _snowcloakConfigService.Current.DisableOptionalPluginWarnings,
                ShownHeelsWarning = _snowcloakConfigService.Current.DisableOptionalPluginWarnings,
                ShownHonorificWarning = _snowcloakConfigService.Current.DisableOptionalPluginWarnings,
                ShowPetNicknamesWarning = _snowcloakConfigService.Current.DisableOptionalPluginWarnings,
                ShownMoodlesWarning = _snowcloakConfigService.Current.DisableOptionalPluginWarnings
            };
        }

        List<string> unavailablePluginsForData = [];
        if (changes.Contains(PlayerChanges.Heels) && !warning.ShownHeelsWarning
            && TryAddUnavailablePlugin(unavailablePluginsForData, "SimpleHeels", _ipcManager.GetStatus(IpcManager.HeelsIpcName)))
        {
            warning.ShownHeelsWarning = true;
        }
        if (changes.Contains(PlayerChanges.Customize) && !warning.ShownCustomizePlusWarning
            && TryAddUnavailablePlugin(unavailablePluginsForData, "Customize+", _ipcManager.GetStatus(IpcManager.CustomizePlusIpcName)))
        {
            warning.ShownCustomizePlusWarning = true;
        }

        if (changes.Contains(PlayerChanges.Honorific) && !warning.ShownHonorificWarning
            && TryAddUnavailablePlugin(unavailablePluginsForData, "Honorific", _ipcManager.GetStatus(IpcManager.HonorificIpcName)))
        {
            warning.ShownHonorificWarning = true;
        }

        if (changes.Contains(PlayerChanges.PetNames) && !warning.ShowPetNicknamesWarning
            && TryAddUnavailablePlugin(unavailablePluginsForData, "PetNicknames", _ipcManager.GetStatus(IpcManager.PetNamesIpcName)))
        {
            warning.ShowPetNicknamesWarning = true;
        }

        if (changes.Contains(PlayerChanges.Moodles) && !warning.ShownMoodlesWarning
            && TryAddUnavailablePlugin(unavailablePluginsForData, "Moodles", _ipcManager.GetStatus(IpcManager.MoodlesIpcName)))
        {
            warning.ShownMoodlesWarning = true;
        }

        if (unavailablePluginsForData.Count > 0)
        {
            var title = "Unavailable optional plugins for {0}";
            var content = "Received data for {0} that needs optional plugins. Install, enable, or update {1} to experience their character fully.";
            Mediator.Publish(new NotificationMessage(string.Format(CultureInfo.InvariantCulture, title, playerName),
                string.Format(CultureInfo.InvariantCulture, content, playerName, string.Join(", ", unavailablePluginsForData)),
                NotificationType.Warning, TimeSpan.FromSeconds(10)));
        }
    }

    private static bool TryAddUnavailablePlugin(List<string> plugins, string displayName, IpcStatus status)
    {
        if (status.IsAvailable)
        {
            return false;
        }

        plugins.Add(string.Format(CultureInfo.InvariantCulture, "{0} ({1})", displayName, DescribeIpcReason(status)));
        return true;
    }

    private static string DescribeIpcReason(IpcStatus status)
        => status.State switch
        {
            IpcState.Missing => "missing",
            IpcState.Disabled => "disabled",
            IpcState.VersionMismatch => "unsupported version",
            IpcState.Error => "error",
            _ => "unavailable",
        };

    private void ClearWarning(UserData? user)
    {
        if (user == null)
        {
            _cachedOptionalPluginWarnings.Clear();
            return;
        }

        _cachedOptionalPluginWarnings.TryRemove(user, out _);
    }
}
