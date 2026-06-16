using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Comparer;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Group;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class CommunitySyncshellWindow : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private GroupDirectoryListResponseDto? _directoryResults;
    private string _directorySearch = string.Empty;
    private string _directoryStatus = string.Empty;
    private string _regionFilter = string.Empty;
    private ushort _worldFilter;

    public CommunitySyncshellWindow(ILogger<CommunitySyncshellWindow> logger, SnowMediator mediator,
        ApiController apiController, PairManager pairManager, DalamudUtilService dalamudUtilService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Community Syncshells###SnowcloakCommunitySyncshells", performanceCollectorService)
    {
        _apiController = apiController;
        _pairManager = pairManager;
        _dalamudUtilService = dalamudUtilService;
        SetScaledSizeConstraints(new Vector2(480, 400), new Vector2(900, 1600));
    }

    public override void OnOpen()
    {
        // Default the location filter to the player's own world so they see nearby shells first.
        ApplyDefaultLocationFilter();
        // Refresh on each open so the listing reflects the latest server state.
        RefreshDirectoryResults();
    }

    private void ApplyDefaultLocationFilter()
    {
        var worldId = (uint)_dalamudUtilService.GetHomeWorldId();
        var region = _dalamudUtilService.GetWorldRegion(worldId);
        if (worldId == 0 || string.IsNullOrEmpty(region))
        {
            return;
        }

        _worldFilter = (ushort)worldId;
        _regionFilter = region;
    }

    protected override void DrawInternal()
    {
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var buttonSize = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Search);
        ImGui.SetNextItemWidth(contentWidth - buttonSize.X - ImGui.GetStyle().ItemSpacing.X);
        var searchChanged = ImGui.InputTextWithHint("##directorysearch", "Search community syncshells", ref _directorySearch, 80, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ElezenImgui.IconButton(FontAwesomeIcon.Search) || searchChanged)
        {
            RefreshDirectoryResults();
        }
        ElezenImgui.AttachTooltip("Search");

        DrawLocationFilter();

        if (!string.IsNullOrWhiteSpace(_directoryStatus))
        {
            ElezenImgui.ColouredWrappedText(_directoryStatus, ImGuiColors.DalamudYellow);
        }

        var results = _directoryResults;
        if (results == null)
        {
            return;
        }

        ImGui.Separator();
        foreach (var entry in results.Entries)
        {
            DrawDirectoryEntry(entry);
        }
    }

    private void RefreshDirectoryResults()
    {
        try
        {
            // A specific world implies its region; the server gates on Region, then narrows by WorldId.
            var region = _regionFilter;
            if (_worldFilter != 0 && string.IsNullOrEmpty(region))
            {
                region = _dalamudUtilService.GetWorldRegion(_worldFilter) ?? string.Empty;
            }

            _directoryResults = _apiController.GroupDirectoryList(new GroupDirectoryQueryDto
            {
                Search = _directorySearch,
                Take = 20,
                Region = string.IsNullOrEmpty(region) ? null : region,
                WorldId = _worldFilter == 0 ? null : _worldFilter
            }).Result;
            _directoryStatus = _directoryResults.Total == 0 ? "No community syncshells found." : string.Empty;
        }
        catch (Exception ex)
        {
            _directoryResults = new GroupDirectoryListResponseDto([], 0);
            _directoryStatus = "Unable to load community syncshells: " + ex.Message;
        }
    }

    private void DrawLocationFilter()
    {
        var regions = _dalamudUtilService.WorldRegions;
        if (regions.Count == 0)
        {
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Location");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(150f * ImGuiHelpers.GlobalScale);
        var regionLabel = string.IsNullOrEmpty(_regionFilter) ? "All regions" : _regionFilter;
        if (ImGui.BeginCombo("##regionfilter", regionLabel))
        {
            if (ImGui.Selectable("All regions", string.IsNullOrEmpty(_regionFilter)))
            {
                SetRegionFilter(string.Empty);
            }

            foreach (var region in regions)
            {
                if (ImGui.Selectable(region, string.Equals(region, _regionFilter, StringComparison.Ordinal)))
                {
                    SetRegionFilter(region);
                }
            }

            ImGui.EndCombo();
        }
        ElezenImgui.AttachTooltip("Only show syncshells based in this region. Syncshells with no location set are always shown.");

        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrEmpty(_regionFilter)))
        {
            ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
            var worldLabel = _worldFilter == 0 ? "All worlds" : _dalamudUtilService.GetWorldName(_worldFilter) ?? "All worlds";
            if (ImGui.BeginCombo("##worldfilter", worldLabel))
            {
                if (ImGui.Selectable("All worlds", _worldFilter == 0))
                {
                    _worldFilter = 0;
                    RefreshDirectoryResults();
                }

                if (!string.IsNullOrEmpty(_regionFilter))
                {
                    foreach (var world in _dalamudUtilService.GetWorldsInRegion(_regionFilter))
                    {
                        if (ImGui.Selectable(world.Name, world.Id == _worldFilter))
                        {
                            _worldFilter = world.Id;
                            RefreshDirectoryResults();
                        }
                    }
                }

                ImGui.EndCombo();
            }
        }
        ElezenImgui.AttachTooltip("Only show syncshells based on this world.");

        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.LocationArrow, "My world"))
        {
            ApplyDefaultLocationFilter();
            RefreshDirectoryResults();
        }
        ElezenImgui.AttachTooltip("Filter to your own home world.");

        if (!string.IsNullOrEmpty(_regionFilter) || _worldFilter != 0)
        {
            ImGui.SameLine();
            if (ElezenImgui.IconButton(FontAwesomeIcon.Times))
            {
                _regionFilter = string.Empty;
                _worldFilter = 0;
                RefreshDirectoryResults();
            }
            ElezenImgui.AttachTooltip("Clear the location filter.");
        }
    }

    private void SetRegionFilter(string region)
    {
        _regionFilter = region;

        // Drop a single-world selection that no longer belongs to the chosen region.
        if (_worldFilter != 0
            && (string.IsNullOrEmpty(region)
                || !string.Equals(_dalamudUtilService.GetWorldRegion(_worldFilter), region, StringComparison.Ordinal)))
        {
            _worldFilter = 0;
        }

        RefreshDirectoryResults();
    }

    private void DrawDirectoryEntry(GroupDirectoryEntryDto entry)
    {
        var alreadyJoined = _pairManager.Groups.Keys.Any(g => GroupDataComparer.Instance.Equals(g, entry.Group));
        var canJoin = entry.JoinPolicy == GroupDirectoryJoinPolicy.Open && !alreadyJoined;
        var title = entry.Group.AliasOrGID;

        using var id = ImRaii.PushId("directory-" + entry.Group.GID);
        ImGui.AlignTextToFramePadding();
        ElezenImgui.ShowIcon(entry.IsVenue ? FontAwesomeIcon.MapMarkedAlt : FontAwesomeIcon.Users, SnowcloakColours.OnlineBlue);
        ImGui.SameLine();
        ImGui.TextColored(Colour.HexToVector4(entry.Group.DisplayColour), title);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
        {
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "{0} members", entry.MemberCountBucket));
        }

        if (canJoin)
        {
            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Join listed Syncshell"))
            {
                var joined = _apiController.GroupDirectoryJoin(new GroupDto(entry.Group)).Result;
                _directoryStatus = joined
                    ? string.Format(CultureInfo.CurrentCulture, "Joined {0}.", title)
                    : string.Format(CultureInfo.CurrentCulture, "Could not join {0}.", title);
                RefreshDirectoryResults();
            }
        }

        var locationText = _dalamudUtilService.GetWorldName(entry.MainWorldId) ?? entry.MainRegion;
        if (!string.IsNullOrEmpty(locationText))
        {
            ImGui.AlignTextToFramePadding();
            ElezenImgui.ShowIcon(FontAwesomeIcon.GlobeAmericas, SnowcloakColours.CompactTextMuted);
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
            {
                ImGui.TextUnformatted(locationText);
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            ElezenImgui.WrappedText(entry.Description);
        }

        if (entry.Tags.Count > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
            {
                ImGui.TextUnformatted(string.Join("  ", entry.Tags.Select(t => "#" + t)));
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.Motd))
        {
            ElezenImgui.WrappedText(entry.Motd);
        }

        foreach (var shellEvent in entry.Events.OrderBy(e => e.StartsAtUtc).Take(2))
        {
            ImGui.AlignTextToFramePadding();
            ElezenImgui.ShowIcon(FontAwesomeIcon.Calendar, SnowcloakColours.CompactTextMuted);
            ImGui.SameLine();
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "{0:g}  {1}", shellEvent.StartsAtUtc.ToLocalTime(), shellEvent.Title));
        }

        ImGui.Separator();
    }
}
