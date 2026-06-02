using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration.Models;
using Snowcloak.Services;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using System;
using System.Linq;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class PairingAvailabilityWindow : WindowMediatorSubscriberBase
{
    private readonly PairRequestService _pairRequestService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SnowProfileManager _profileManager;
    private bool _locked;
    private List<AvailabilityEntry> _lockedEntries = new();
    private readonly TitleBarButton _lockButton;
    private string _lockTooltip;
    
    public PairingAvailabilityWindow(ILogger<PairingAvailabilityWindow> logger, SnowMediator mediator,
        PairRequestService pairRequestService, DalamudUtilService dalamudUtilService, SnowProfileManager profileManager,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "SnowcloakPairingAvailability", performanceCollectorService)
    {
        _pairRequestService = pairRequestService;
        _dalamudUtilService = dalamudUtilService;
        _profileManager = profileManager;

        _lockTooltip = "Lock list to pause updates";
        
        SizeConstraints = new()
        {
            MinimumSize = new(350, 200)
        };

        RespectCloseHotkey = true;
        TitleBarButtons.Add(new TitleBarButton()
        {
            ShowTooltip = () => ImGui.SetTooltip("Refresh list of nearby players"),
            Click = (btn) => _ = RefreshAndUpdateLockAsync(),
            Icon = FontAwesomeIcon.SyncAlt
        });
        
        
        _lockButton = new TitleBarButton()
        {
            ShowTooltip = () => ImGui.SetTooltip(_lockTooltip),
            Click = _ => ToggleLock(),
            Icon = FontAwesomeIcon.LockOpen
        };

        TitleBarButtons.Add(_lockButton);
        WindowName = "Frostbrand: nearby players open to pairing";
    }

    protected override void DrawInternal()
    {
        var availabilitySnapshot = _pairRequestService.GetAvailabilityFilterSnapshot();
        var filteredCount = availabilitySnapshot.FilteredCount;
        var available = _locked
            ? _lockedEntries
            : BuildAvailabilityEntries();

        if (available.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No nearby Frostbrand users are currently open to pairing.");
            if (filteredCount > 0)
                ImGui.TextColored(ImGuiColors.DalamudGrey,
                    string.Format("({0} nearby players filtered by auto-reject settings)", filteredCount));
            return;
        }

        using var table = ImRaii.Table("pairing-availability-table", 6, ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders | ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Resizable);
        if (table) {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 0.22f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 0.14f);
            ImGui.TableSetupColumn("Tagline", ImGuiTableColumnFlags.WidthStretch, 0.3f);
            ImGui.TableSetupColumn("Pronouns", ImGuiTableColumnFlags.WidthStretch, 0.12f);
            ImGui.TableSetupColumn("Approach", ImGuiTableColumnFlags.WidthStretch, 0.14f);
            ImGui.TableSetupColumn("Homeworld", ImGuiTableColumnFlags.WidthStretch, 0.16f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            ImGuiClip.ClippedDraw(available, this.DrawPlayer, ImGui.GetTextLineHeightWithSpacing());
        }
    }

    private void DrawPlayer(AvailabilityEntry entry)
    {
                ImGui.TableNextColumn();
                
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.Profile?.CharacterName) ? entry.DisplayName : entry.Profile.CharacterName);
                this.DrawContextMenu(entry);
                ImGui.TableNextColumn();
                var status = string.IsNullOrWhiteSpace(entry.Profile?.RpStatus) ? "Open to pairing" : entry.Profile.RpStatus;
                ImGui.TextColored(ImGuiColors.HealerGreen, status);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.Profile?.Tagline) ? "-" : entry.Profile.Tagline);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.Profile?.Pronouns) ? "-" : entry.Profile.Pronouns);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.Profile?.Approachability) ? "-" : entry.Profile.Approachability);
                ImGui.TableNextColumn();
                var worldName = entry.HomeWorldId.HasValue
                                && _dalamudUtilService.WorldData.Value.TryGetValue(entry.HomeWorldId.Value, out var world)
                    ? world
                    : string.Empty;
                ImGui.TextUnformatted(worldName);
    }

    private void DrawContextMenu(AvailabilityEntry entry)
    {
        var worldName = entry.HomeWorldId.HasValue
                        && _dalamudUtilService.WorldData.Value.TryGetValue(entry.HomeWorldId.Value, out var world)
            ? world
            : string.Empty;
        using var popupContext = ImRaii.ContextPopupItem($"{entry.DisplayName}{worldName}##SCPopupCX");
        if (popupContext)
        {
            if (ImGui.Selectable("Examine"))
            {
                _ = HandleExamineAsync(entry);
            }

            if (ImGui.Selectable("Adventurer Plate"))
            {
                _ = HandleAdventurerPlateAsync(entry);
            }

            if (ImGui.Selectable("View Snowcloak Profile"))
            {
                _ = _pairRequestService.RequestProfileAsync(entry.Ident);
            }

            if (ImGui.Selectable("Send Snowcloak Pair Request"))
            {
                _ = _pairRequestService.SendPairRequestAsync(entry.Ident);
            }

        }
    }
    
    private async Task HandleExamineAsync(AvailabilityEntry entry)
    {
        var success = await _dalamudUtilService.ExaminePlayerByIdentAsync(entry.Ident).ConfigureAwait(false);

        Mediator.Publish(success
            ? new NotificationMessage("Examine",
                string.Format("Opening examination for {0}.", entry.DisplayName), NotificationType.Info, TimeSpan.FromSeconds(4))
            : new NotificationMessage("Examine failed",
                "Could not find that player nearby.", NotificationType.Warning, TimeSpan.FromSeconds(4)));
    }

    private async Task HandleAdventurerPlateAsync(AvailabilityEntry entry)
    {
        var success = await _dalamudUtilService.OpenAdventurerPlateByIdentAsync(entry.Ident).ConfigureAwait(false);

        if (!success)
        {
            Mediator.Publish(new NotificationMessage(
                "Adventurer Plate failed",
                "Could not find that player nearby.", NotificationType.Warning,
                TimeSpan.FromSeconds(4)));
        }
    }
    
    private void ToggleLock()
    {
        _locked = !_locked;
        _lockButton.Icon = _locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen;
        _lockTooltip = _locked
            ? "Unlock to resume live updates"
            : "Lock list to pause updates";

        if (_locked)
            _lockedEntries = BuildAvailabilityEntries();
    }

    private List<AvailabilityEntry> BuildAvailabilityEntries()
    {
        var availability = _pairRequestService.GetAvailabilityFilterSnapshot();

        return availability.Accepted
            .Select(ident => (ident, pc: _dalamudUtilService.FindPlayerByNameHash(ident)))
            .Where(tuple => tuple.pc.EntityId != 0 && tuple.pc.Address != IntPtr.Zero)
            .Select(tuple => new AvailabilityEntry(
                tuple.ident,
                string.IsNullOrWhiteSpace(tuple.pc.Name) ? tuple.ident : tuple.pc.Name,
                tuple.pc.HomeWorldId != 0 ? (ushort?)tuple.pc.HomeWorldId : null,
                _profileManager.GetSummary(tuple.ident)))
            .OrderBy(entry => entry.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    private async Task RefreshAndUpdateLockAsync()
    {
        await _pairRequestService.RefreshNearbyAvailabilityAsync(force: true).ConfigureAwait(false);

        if (_locked)
            _lockedEntries = BuildAvailabilityEntries();
    }

    private readonly record struct AvailabilityEntry(string Ident, string DisplayName, ushort? HomeWorldId, CharacterProfileSummaryDto? Profile);
    
}
