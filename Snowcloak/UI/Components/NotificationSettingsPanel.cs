using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;

namespace Snowcloak.UI.Components;

public sealed class NotificationSettingsPanel
{
    private readonly SnowcloakConfigService _configService;
    private readonly UiFontService _fontService;
    private readonly Dictionary<string, object> _selectedComboItems = new(StringComparer.Ordinal);

    public NotificationSettingsPanel(SnowcloakConfigService configService, UiFontService fontService)
    {
        _configService = configService;
        _fontService = fontService;
    }

    public void Draw()
    {
        var disableOptionalPluginWarnings = _configService.Current.DisableOptionalPluginWarnings;
        var onlineNotifs = _configService.Current.ShowOnlineNotifications;
        var onlineNotifsPairsOnly = _configService.Current.ShowOnlineNotificationsOnlyForIndividualPairs;
        var onlineNotifsNamedOnly = _configService.Current.ShowOnlineNotificationsOnlyForNamedPairs;

        _fontService.BigText("Notifications");

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        SettingsUiControls.DrawCombo("Info Notification Display##settingsUi", Enum.GetValues<NotificationLocation>(), i => i.ToString(),
            _selectedComboItems,
            i => _configService.Update(c => c.InfoNotification = i),
            _configService.Current.InfoNotification);
        ElezenImgui.DrawHelpText("The location where \"Info\" notifications will display."
            + Environment.NewLine + "'Nowhere' will not show any Info notifications"
            + Environment.NewLine + "'Chat' will print Info notifications in chat"
            + Environment.NewLine + "'Toast' will show Warning toast notifications in the bottom right corner"
            + Environment.NewLine + "'Both' will show chat as well as the toast notification");

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        SettingsUiControls.DrawCombo("Warning Notification Display##settingsUi", Enum.GetValues<NotificationLocation>(), i => i.ToString(),
            _selectedComboItems,
            i => _configService.Update(c => c.WarningNotification = i),
            _configService.Current.WarningNotification);
        ElezenImgui.DrawHelpText("The location where \"Warning\" notifications will display."
            + Environment.NewLine + "'Nowhere' will not show any Warning notifications"
            + Environment.NewLine + "'Chat' will print Warning notifications in chat"
            + Environment.NewLine + "'Toast' will show Warning toast notifications in the bottom right corner"
            + Environment.NewLine + "'Both' will show chat as well as the toast notification");

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        SettingsUiControls.DrawCombo("Error Notification Display##settingsUi", Enum.GetValues<NotificationLocation>(), i => i.ToString(),
            _selectedComboItems,
            i => _configService.Update(c => c.ErrorNotification = i),
            _configService.Current.ErrorNotification);
        ElezenImgui.DrawHelpText("The location where \"Error\" notifications will display."
            + Environment.NewLine + "'Nowhere' will not show any Error notifications"
            + Environment.NewLine + "'Chat' will print Error notifications in chat"
            + Environment.NewLine + "'Toast' will show Error toast notifications in the bottom right corner"
            + Environment.NewLine + "'Both' will show chat as well as the toast notification");

        if (ImGui.Checkbox("Disable optional plugin warnings", ref disableOptionalPluginWarnings))
        {
            _configService.Update(c => c.DisableOptionalPluginWarnings = disableOptionalPluginWarnings);
        }
        ElezenImgui.DrawHelpText("Enabling this will not show any \"Warning\" labeled messages for missing optional plugins.");

        if (ImGui.Checkbox("Enable online notifications", ref onlineNotifs))
        {
            _configService.Update(c => c.ShowOnlineNotifications = onlineNotifs);
        }
        ElezenImgui.DrawHelpText("Enabling this will show a small notification (type: Info) in the bottom right corner when pairs go online.");

        using (ImRaii.Disabled(!onlineNotifs))
        {
            using var indent = ImRaii.PushIndent();
            if (ImGui.Checkbox("Notify only for individual pairs", ref onlineNotifsPairsOnly))
            {
                _configService.Update(c => c.ShowOnlineNotificationsOnlyForIndividualPairs = onlineNotifsPairsOnly);
            }
            ElezenImgui.DrawHelpText("Enabling this will only show online notifications (type: Info) for individual pairs.");

            if (ImGui.Checkbox("Notify only for named pairs", ref onlineNotifsNamedOnly))
            {
                _configService.Update(c => c.ShowOnlineNotificationsOnlyForNamedPairs = onlineNotifsNamedOnly);
            }
            ElezenImgui.DrawHelpText("Enabling this will only show online notifications (type: Info) for pairs where you have set an individual note.");
        }

        ImGui.Separator();
        _fontService.BigText("Server News");
        var disableServerNewsInChat = _configService.Current.DisableServerNewsInChat;
        if (ImGui.Checkbox("Disable server news posts in chat", ref disableServerNewsInChat))
        {
            _configService.Update(c => c.DisableServerNewsInChat = disableServerNewsInChat);
        }
        ElezenImgui.DrawHelpText("Stops Snowcloak server news announcements from being posted to in-game chat.");
    }
}
