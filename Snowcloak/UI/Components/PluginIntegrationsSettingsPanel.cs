using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Interop.Ipc;
using Snowcloak.Ipc;
using Snowcloak.Services;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI.Components;

public sealed class PluginIntegrationsSettingsPanel
{
    private static readonly (SnowcloakIpcCapability Permission, string Label, string Description)[] PermissionOptions =
    [
        (SnowcloakIpcCapability.ReadPairData, "Read visible pair data", "Reads internal object IDs, pair identities, aliases, visibility and data application state."),
        (SnowcloakIpcCapability.ReadPairDataOutOfRange, "Read out-of-range pair data", "List paired users and inspect their online, paused and synchronisation state when they are not visible."),
        (SnowcloakIpcCapability.ReadProfileData, "Read profile summaries", "Reads profile summaries and profile update activity."),
        (SnowcloakIpcCapability.OpenProfileWindow, "Open profile windows", "Allows opening Snowcloak's profile window for a pair."),
        (SnowcloakIpcCapability.ApplyMcdf, "Apply MCDF files", "Apply an MCDF file to a GPose object. Snowcloak still restricts this to GPose."),
        (SnowcloakIpcCapability.TransmitExtensionData, "Transmit extension data", "Allows the plugin to package its data into your pair packet."),
        (SnowcloakIpcCapability.ReceiveExtensionData, "Receive extension data", "Allows the plugin to act on and receive its data from your pairs."),
        (SnowcloakIpcCapability.ReceiveExtensionDataOutOfRange, "Receive out-of-range extension data", "Continue receiving only this plugin's matching data while an online pair is outside game-object range. Snowcloak does not download the pair's appearance sections for this."),
        (SnowcloakIpcCapability.OpenPairRequestWindow, "Open pair-request confirmation", "Open a Frostbrand pair request confirmation for a targeted player. The plugin cannot send the request directly."),
    ];

    private readonly UiFontService _fontService;
    private readonly PublicIpcProvider _publicIpcProvider;
    private IReadOnlyList<PluginIntegrationDiagnostic> _integrations = [];
    private DateTimeOffset _nextRefreshUtc;

    public PluginIntegrationsSettingsPanel(
        UiFontService fontService,
        PublicIpcProvider publicIpcProvider)
    {
        _fontService = fontService;
        _publicIpcProvider = publicIpcProvider;
    }

    public void Draw()
    {
        _fontService.BigText("Plugin Integrations");
        ElezenImgui.WrappedText("Review what each companion plugin has requested, approve only the capabilities you want it to use, and inspect its current activity.");
        ElezenImgui.DrawHelpText("New plugins start without permissions. A plugin can query its current grant and is notified whenever you change it.");

        if (DateTimeOffset.UtcNow >= _nextRefreshUtc)
        {
            RefreshDiagnostics();
        }

        DrawOverview(_integrations);

        ImGui.Separator();
        _fontService.BigText("Detected Plugin Integrations");
        if (_integrations.Count == 0)
        {
            ElezenImgui.ColouredWrappedText("No plugin has requested Snowcloak IPC access yet.", ImGuiColors.DalamudGrey);
            return;
        }

        foreach (var integration in _integrations)
        {
            DrawIntegration(integration);
        }
    }

    private static void DrawOverview(IReadOnlyList<PluginIntegrationDiagnostic> integrations)
    {
        var relayedBytes = integrations
            .Where(integration => Allows(integration.GrantedPermissions, SnowcloakIpcCapability.TransmitExtensionData))
            .Sum(static integration => (long)integration.OutgoingBytes);
        var incomingBytes = integrations.Sum(static integration => integration.IncomingBytes);

        ImGuiHelpers.ScaledDummy(4f);
        if (!ImGui.BeginTable("PluginIntegrationOverview", 3, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        DrawMetric("Registered slots", $"{integrations.Count(static integration => integration.IsRegistered):N0} / {PublicIpcProvider.MaxRegisteredPlugins:N0}");
        DrawMetric("Current outgoing", $"{FormatBytes(relayedBytes)} / {FormatBytes(PublicIpcProvider.MaxTotalBytes)}");
        DrawMetric("Current incoming", FormatBytes(incomingBytes));
        ImGui.EndTable();
    }

    private static void DrawMetric(string label, string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.TextUnformatted(value);
    }

    private void DrawIntegration(PluginIntegrationDiagnostic integration)
    {
        using var id = ImRaii.PushId(integration.PluginKey);
        ImGui.Separator();
        var pendingPermissions = integration.RequestedPermissions & ~integration.GrantedPermissions;
        var loadedState = integration.IsLoaded ? "Loaded" : "Unloaded";
        var registrationState = integration.IsRegistered ? "Registered" : "Remembered grant";
        var approvalState = pendingPermissions == SnowcloakIpcCapability.None ? "Approved" : "Approval required";
        var headerFlags = pendingPermissions == SnowcloakIpcCapability.None
            ? ImGuiTreeNodeFlags.None
            : ImGuiTreeNodeFlags.DefaultOpen;
        if (!ImGui.CollapsingHeader(
                $"{integration.PluginName}  |  {loadedState}  |  {registrationState}  |  {approvalState}###IntegrationDetails",
                headerFlags))
        {
            return;
        }

        ImGui.TextColored(integration.IsLoaded ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed, loadedState);
        ImGui.SameLine();
        ImGui.TextColored(integration.IsRegistered ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey,
            integration.IsRegistered ? "Registered this session" : registrationState);
        var identity = string.IsNullOrEmpty(integration.Version)
            ? integration.PluginKey
            : $"{integration.PluginKey}  |  {integration.Version}";
        ImGui.TextColored(ImGuiColors.DalamudGrey, identity);

        if (pendingPermissions == SnowcloakIpcCapability.None)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "All requested permissions approved");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Approval required");
        }

        foreach (var option in PermissionOptions)
        {
            if (!Allows(integration.RequestedPermissions, option.Permission))
            {
                continue;
            }

            var granted = Allows(integration.GrantedPermissions, option.Permission);
            if (ImGui.Checkbox(option.Label, ref granted))
            {
                _publicIpcProvider.SetPluginPermission(integration.PluginKey, option.Permission, granted);
                _nextRefreshUtc = DateTimeOffset.MinValue;
            }
            ElezenImgui.DrawHelpText(option.Description);
        }

        var outgoingRatio = Math.Clamp(integration.OutgoingBytes / (float)PublicIpcProvider.MaxBytesPerPlugin, 0f, 1f);
        ImGui.ProgressBar(outgoingRatio, new Vector2(-1f, 0f),
            $"Outgoing slot {FormatBytes(integration.OutgoingBytes)} / {FormatBytes(PublicIpcProvider.MaxBytesPerPlugin)}");

        var pairLabel = integration.IncomingPairs == 1 ? "pair" : "pairs";
        ImGui.TextUnformatted($"Transmitted this session: {FormatBytes(integration.TransmittedBytes)}");
        ImGui.TextUnformatted($"Received this session: {FormatBytes(integration.ReceivedBytes)}; active payload {FormatBytes(integration.IncomingBytes)} from {integration.IncomingPairs:N0} {pairLabel}");
        ImGui.TextUnformatted($"Last local update: {FormatTime(integration.LastLocalChangeAt)}");
        ImGui.TextUnformatted($"Last transmitted: {FormatTime(integration.LastTransmittedAt)}");
        ImGui.TextUnformatted($"Last received: {FormatTime(integration.LastReceivedAt)}");
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"Last used: {FormatTime(Latest(integration.LastLocalChangeAt, integration.LastTransmittedAt, integration.LastReceivedAt))}");
    }

    private static bool Allows(SnowcloakIpcCapability value, SnowcloakIpcCapability permission)
        => (value & permission) == permission;

    private void RefreshDiagnostics()
    {
        _integrations = _publicIpcProvider.GetPluginIntegrationDiagnostics();
        _nextRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(1);
    }

    private static DateTimeOffset? Latest(params DateTimeOffset?[] values)
    {
        DateTimeOffset? latest = null;
        foreach (var value in values)
        {
            if (value.HasValue && (!latest.HasValue || value.Value > latest.Value))
            {
                latest = value;
            }
        }
        return latest;
    }

    private static string FormatTime(DateTimeOffset? value)
        => value.HasValue ? value.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "Never";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:N0} B", bytes);
        }

        if (bytes < 1024 * 1024)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.##} KiB", bytes / 1024d);
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} MiB", bytes / (1024d * 1024d));
    }
}
