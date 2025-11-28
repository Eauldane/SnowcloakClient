using Snowcloak.API.Data;
using Snowcloak.API.Data.Comparer;
using Snowcloak.Interop.Ipc;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Data;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using System.Collections.Concurrent;

namespace Snowcloak.Services;

public class PluginWarningNotificationService
{
    private readonly ConcurrentDictionary<UserData, OptionalPluginWarning> _cachedOptionalPluginWarnings = new(UserDataComparer.Instance);
    private readonly IpcManager _ipcManager;
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly SnowMediator _mediator;

    public PluginWarningNotificationService(SnowcloakConfigService snowcloakConfigService, IpcManager ipcManager, SnowMediator mediator)
    {
        _snowcloakConfigService = snowcloakConfigService;
        _ipcManager = ipcManager;
        _mediator = mediator;
    }

    public void NotifyForMissingPlugins(UserData user, string playerName, HashSet<PlayerChanges> changes)
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

        List<string> missingPluginsForData = [];
        if (changes.Contains(PlayerChanges.Heels) && !warning.ShownHeelsWarning && !_ipcManager.Heels.APIAvailable)
        {
            missingPluginsForData.Add("SimpleHeels");
            warning.ShownHeelsWarning = true;
        }
        if (changes.Contains(PlayerChanges.Customize) && !warning.ShownCustomizePlusWarning && !_ipcManager.CustomizePlus.APIAvailable)
        {
            missingPluginsForData.Add("Customize+");
            warning.ShownCustomizePlusWarning = true;
        }

        if (changes.Contains(PlayerChanges.Honorific) && !warning.ShownHonorificWarning && !_ipcManager.Honorific.APIAvailable)
        {
            missingPluginsForData.Add("Honorific");
            warning.ShownHonorificWarning = true;
        }

        if (changes.Contains(PlayerChanges.PetNames) && !warning.ShowPetNicknamesWarning && !_ipcManager.PetNames.APIAvailable)
        {
            missingPluginsForData.Add("PetNicknames");
            warning.ShowPetNicknamesWarning = true;
        }

        if (changes.Contains(PlayerChanges.Moodles) && !warning.ShownMoodlesWarning && !_ipcManager.Moodles.APIAvailable)
        {
            missingPluginsForData.Add("Moodles");
            warning.ShownMoodlesWarning = true;
        }

        if (missingPluginsForData.Any())
        {
            _mediator.Publish(new NotificationMessage("Missing plugins for " + playerName,
                $"Received data for {playerName} that contained information for plugins you have not installed. Install {string.Join(", ", missingPluginsForData)} to experience their character fully.",
                NotificationType.Warning, TimeSpan.FromSeconds(10)));
        }
    }
}