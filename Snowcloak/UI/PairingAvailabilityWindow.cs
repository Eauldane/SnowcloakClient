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
using Snowcloak.Services.Localisation;

namespace Snowcloak.UI;

public sealed class PairingAvailabilityWindow : WindowMediatorSubscriberBase
{
    private readonly PairRequestService _pairRequestService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly LocalisationService _localisationService;
    private bool _locked;
    private List<AvailabilityEntry> _lockedEntries = new();
    private readonly TitleBarButton _lockButton;
    private string _lockTooltip;
    
    public PairingAvailabilityWindow(ILogger<PairingAvailabilityWindow> logger, SnowMediator mediator,
        PairRequestService pairRequestService, DalamudUtilService dalamudUtilService,
        PerformanceCollectorService performanceCollectorService, LocalisationService localisationService)
        : base(logger, mediator, "SnowcloakPairingAvailability", performanceCollectorService)
    {
        _pairRequestService = pairRequestService;
        _dalamudUtilService = dalamudUtilService;
        _localisationService = localisationService;

        _lockTooltip = L("LockTooltipUnlocked", "Lock list to pause updates");
        
        SizeConstraints = new()
        {
            MinimumSize = new(350, 200)
        };

        RespectCloseHotkey = true;
        TitleBarButtons.Add(new TitleBarButton()
        {
            ShowTooltip = () => ImGui.SetTooltip(L("RefreshTooltip", "Refresh list of nearby players")),
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
        WindowName = L("WindowTitle", "Nearby players open to pairing");
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
            ImGui.TextColored(ImGuiColors.DalamudGrey, L("NoNearbyUsers", "No nearby users are currently open to pairing."));
            if (filteredCount > 0)
                ImGui.TextColored(ImGuiColors.DalamudGrey,
                    string.Format(L("FilteredPlayers", "({0} nearby players filtered by auto-reject settings)"), filteredCount));
            return;
        }

        using var table = ImRaii.Table("pairing-availability-table", 6, ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders | ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Resizable);
        if (table) {
            ImGui.TableSetupColumn(L("Column.Name", "Name"), ImGuiTableColumnFlags.WidthStretch, 0.28f);
            ImGui.TableSetupColumn(L("Column.Homeworld", "Homeworld"), ImGuiTableColumnFlags.WidthStretch, 0.18f);
            ImGui.TableSetupColumn(L("Column.Class", "Class"), ImGuiTableColumnFlags.WidthStretch, 0.16f);
            ImGui.TableSetupColumn(L("Column.Level", "Level"), ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn(L("Column.Gender", "Gender"), ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn(L("Column.Clan", "Clan"), ImGuiTableColumnFlags.WidthStretch, 0.2f);
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
                    0 => L("Gender.Male", "Male"),
                    1 => L("Gender.Female", "Female"),
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
            if (ImGui.Selectable(L("Context.Examine", "Examine")))
            {
                _ = HandleExamineAsync(entry);
            }

            if (ImGui.Selectable(L("Context.AdventurerPlate", "Adventurer Plate")))
            {
                _ = HandleAdventurerPlateAsync(entry);
            }

            if (ImGui.Selectable(L("Context.ViewProfile", "View Snowcloak Profile")))
            {
                _ = _pairRequestService.RequestProfileAsync(entry.Ident);
            }

            if (ImGui.Selectable(L("Context.SendPairRequest", "Send Snowcloak Pair Request")))
            {
                _ = _pairRequestService.SendPairRequestAsync(entry.Ident);
            }

        }
    }
    
    private async Task HandleExamineAsync(AvailabilityEntry entry)
    {
        var success = await _dalamudUtilService.ExaminePlayerByIdentAsync(entry.Ident).ConfigureAwait(false);

        Mediator.Publish(success
            ? new NotificationMessage(L("Notification.ExamineTitle", "Examine"),
                string.Format(L("Notification.ExamineOpening", "Opening examination for {0}."), entry.DisplayName), NotificationType.Info, TimeSpan.FromSeconds(4))
            : new NotificationMessage(L("Notification.ExamineFailedTitle", "Examine failed"),
                L("Notification.NotNearby", "Could not find that player nearby."), NotificationType.Warning, TimeSpan.FromSeconds(4)));
    }

    private async Task HandleAdventurerPlateAsync(AvailabilityEntry entry)
    {
        var success = await _dalamudUtilService.OpenAdventurerPlateByIdentAsync(entry.Ident).ConfigureAwait(false);

        if (!success)
        {
            Mediator.Publish(new NotificationMessage(
                L("Notification.AdventurerPlateFailedTitle", "Adventurer Plate failed"),
                L("Notification.NotNearby", "Could not find that player nearby."), NotificationType.Warning,
                TimeSpan.FromSeconds(4)));
        }
    }
    
    private void ToggleLock()
    {
        _locked = !_locked;
        _lockButton.Icon = _locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen;
        _lockTooltip = _locked
            ? L("LockTooltipLocked", "Unlock to resume live updates")
            : L("LockTooltipUnlocked", "Lock list to pause updates");

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
    
    private string L(string key, string fallback)
    {
        return _localisationService.GetString($"PairingAvailabilityWindow.{key}", fallback);
    }
}