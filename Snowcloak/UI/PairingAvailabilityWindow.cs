using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Microsoft.Extensions.Logging;
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
    private bool _locked;
    private List<AvailabilityEntry> _lockedEntries = new();
    private readonly TitleBarButton _lockButton;
    private string _lockTooltip;
    
    public PairingAvailabilityWindow(ILogger<PairingAvailabilityWindow> logger, SnowMediator mediator,
        PairRequestService pairRequestService, DalamudUtilService dalamudUtilService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "SnowcloakPairingAvailability", performanceCollectorService)
    {
        _pairRequestService = pairRequestService;
        _dalamudUtilService = dalamudUtilService;

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
        WindowName = "Nearby players open to pairing";
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
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No nearby users are currently open to pairing.");
            if (filteredCount > 0)
                ImGui.TextColored(ImGuiColors.DalamudGrey,
                    string.Format("({0} nearby players filtered by auto-reject settings)", filteredCount));
            return;
        }

        using var table = ImRaii.Table("pairing-availability-table", 6, ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders | ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Resizable);
        if (table) {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.28f);
            ImGui.TableSetupColumn("Homeworld", ImGuiTableColumnFlags.WidthStretch, 0.18f);
            ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthStretch, 0.16f);
            ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Gender", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Clan", ImGuiTableColumnFlags.WidthStretch, 0.2f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            ImGuiClip.ClippedDraw(available, this.DrawPlayer, ImGui.GetTextLineHeightWithSpacing());
        }
    }

    private void DrawPlayer(AvailabilityEntry entry)
    {
                ImGui.TableNextColumn();
                
                ImGui.TextUnformatted(entry.DisplayName);
                this.DrawContextMenu(entry);
                ImGui.TableNextColumn();
                
                var worldName = entry.HomeWorldId.HasValue
                                && _dalamudUtilService.WorldData.Value.TryGetValue(entry.HomeWorldId.Value, out var world)
                    ? world
                    : string.Empty;
                ImGui.TextUnformatted(worldName);
                ImGui.TableNextColumn();
                
                var className = entry.ClassJobId != 0
                                && _dalamudUtilService.ClassJobAbbreviations.Value.TryGetValue(entry.ClassJobId, out var classJob)
                    ? classJob
                    : string.Empty;
                ImGui.TextUnformatted(string.IsNullOrEmpty(className) ? "-" : className);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Level > 0 ? entry.Level.ToString() : "-");
                
                ImGui.TableNextColumn();
                var gender = entry.Gender switch
                {
                    0 =>"Male",
                    1 => "Female",
                    _ => "-"
                };
                ImGui.TextUnformatted(gender);

                ImGui.TableNextColumn();
                var clan = entry.Tribe != 0
                           && _dalamudUtilService.TribeNames.Value.TryGetValue(entry.Tribe, out var tribe)
                    ? tribe
                    : string.Empty;
                ImGui.TextUnformatted(string.IsNullOrEmpty(clan) ? "-" : clan);
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
            .Where(tuple => tuple.pc.ObjectId != 0 && tuple.pc.Address != IntPtr.Zero)
            .Select(tuple => new AvailabilityEntry(
                tuple.ident,
                string.IsNullOrWhiteSpace(tuple.pc.Name) ? tuple.ident : tuple.pc.Name,
                tuple.pc.HomeWorldId != 0 ? (ushort?)tuple.pc.HomeWorldId : null,
                tuple.pc.ClassJob,
                tuple.pc.Level,
                tuple.pc.Gender,
                tuple.pc.Clan))
            .OrderBy(entry => entry.DisplayName)
            .ToList();
    }

    private async Task RefreshAndUpdateLockAsync()
    {
        await _pairRequestService.RefreshNearbyAvailabilityAsync(force: true).ConfigureAwait(false);

        if (_locked)
            _lockedEntries = BuildAvailabilityEntries();
    }

    private readonly record struct AvailabilityEntry(string Ident, string DisplayName, ushort? HomeWorldId, byte ClassJobId, byte Level, byte Gender, byte Tribe);
    
}