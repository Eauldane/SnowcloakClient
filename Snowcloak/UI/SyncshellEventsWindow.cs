using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Group;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class SyncshellEventsWindow : WindowMediatorSubscriberBase
{
    // An event counts as "active" for one hour after its start time. This mirrors the
    // calendar indicator shown on the syncshell row in the main UI.
    private static readonly TimeSpan EventActiveWindow = TimeSpan.FromHours(1);

    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly AsyncOp<GroupCommunityDto> _communityLoadOperation = new();
    private GroupCommunityDto? _community;
    private string _status = string.Empty;

    public SyncshellEventsWindow(ILogger<SyncshellEventsWindow> logger, SnowMediator mediator,
        ApiController apiController, PairManager pairManager, DalamudUtilService dalamudUtilService,
        GroupFullInfoDto groupFullInfo, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, BuildWindowTitle(groupFullInfo), performanceCollectorService)
    {
        ArgumentNullException.ThrowIfNull(groupFullInfo);
        GroupFullInfo = groupFullInfo;
        _apiController = apiController;
        _pairManager = pairManager;
        _dalamudUtilService = dalamudUtilService;
        IsOpen = true;
        SetScaledSizeConstraints(new Vector2(420, 320), new Vector2(720, 1400));
        StartCommunityLoad();
    }

    public GroupFullInfoDto GroupFullInfo { get; private set; }

    private static string BuildWindowTitle(GroupFullInfoDto groupFullInfo)
    {
        ArgumentNullException.ThrowIfNull(groupFullInfo);
        return string.Format(CultureInfo.CurrentCulture, "Events - {0}###SnowcloakSyncshellEvents_{1}",
            groupFullInfo.GroupAliasOrGID, groupFullInfo.GID);
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    protected override void DrawInternal()
    {
        ConsumeCommunityLoad();

        if (_pairManager.Groups.TryGetValue(GroupFullInfo.Group, out var refreshed))
        {
            GroupFullInfo = refreshed;
        }

        using var id = ImRaii.PushId("syncshell_events_" + GroupFullInfo.GID);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "Events for {0}", GroupFullInfo.GroupAliasOrGID));
        ImGui.SameLine();
        using (ImRaii.Disabled(_communityLoadOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Retweet, "Refresh events"))
            {
                StartCommunityLoad();
            }
        }
        if (_communityLoadOperation.IsRunning)
        {
            ImGui.SameLine();
            ElezenImgui.ColouredText("Refreshing...", ImGuiColors.DalamudYellow);
        }
        ImGui.Separator();

        if (!string.IsNullOrWhiteSpace(_status))
        {
            ElezenImgui.ColouredWrappedText(_status, ImGuiColors.DalamudYellow);
        }

        var community = _community;
        if (community == null)
        {
            if (_communityLoadOperation.IsRunning)
            {
                ElezenImgui.ColouredWrappedText("Loading events...", ImGuiColors.DalamudYellow);
            }

            return;
        }

        var locationText = _dalamudUtilService.GetWorldName(community.MainWorldId) ?? community.MainRegion;
        if (!string.IsNullOrEmpty(locationText))
        {
            ImGui.AlignTextToFramePadding();
            ElezenImgui.ShowIcon(FontAwesomeIcon.GlobeAmericas, SnowcloakColours.CompactTextMuted);
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
            {
                ImGui.TextUnformatted("Location: " + locationText);
            }
            ImGuiHelpers.ScaledDummy(2f);
        }

        var nowUtc = DateTime.UtcNow;
        var active = new List<GroupEventDto>();
        var upcoming = new List<GroupEventDto>();
        foreach (var shellEvent in community.Events)
        {
            var start = DateTime.SpecifyKind(shellEvent.StartsAtUtc, DateTimeKind.Utc);
            if (start <= nowUtc && nowUtc < start + EventActiveWindow)
            {
                active.Add(shellEvent);
            }
            else if (start > nowUtc)
            {
                upcoming.Add(shellEvent);
            }
        }

        if (active.Count == 0 && upcoming.Count == 0)
        {
            ElezenImgui.ColouredWrappedText("No upcoming or active events are scheduled.", ImGuiColors.DalamudGrey);
            return;
        }

        using var child = ImRaii.Child("events_list", new Vector2(-1, -1), false);

        if (active.Count > 0)
        {
            ImGui.TextUnformatted("Happening now");
            ImGui.Separator();
            foreach (var shellEvent in active.OrderBy(e => e.StartsAtUtc))
            {
                DrawEventRow(shellEvent, nowUtc, active: true);
            }

            ImGuiHelpers.ScaledDummy(4f);
        }

        if (upcoming.Count > 0)
        {
            ImGui.TextUnformatted("Upcoming");
            ImGui.Separator();
            foreach (var shellEvent in upcoming.OrderBy(e => e.StartsAtUtc))
            {
                DrawEventRow(shellEvent, nowUtc, active: false);
            }
        }
    }

    private static void DrawEventRow(GroupEventDto shellEvent, DateTime nowUtc, bool active)
    {
        using var id = ImRaii.PushId("event-" + shellEvent.Id.ToString("N"));
        var startUtc = DateTime.SpecifyKind(shellEvent.StartsAtUtc, DateTimeKind.Utc);
        var startLocal = startUtc.ToLocalTime();
        var accent = active ? ImGuiColors.HealerGreen : SnowcloakColours.OnlineBlue;

        ImGui.AlignTextToFramePadding();
        ElezenImgui.ShowIcon(active ? FontAwesomeIcon.CalendarCheck : FontAwesomeIcon.CalendarDay, accent);
        ImGui.SameLine();
        ImGui.TextColored(accent, shellEvent.Title);

        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
        {
            if (active)
            {
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture,
                    "In progress - started {0:g} ({1} ago)", startLocal, FormatDuration(nowUtc - startUtc)));
            }
            else
            {
                ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture,
                    "{0:g} - {1}", startLocal, FormatStartsIn(startUtc - nowUtc)));
            }
        }

        if (!string.IsNullOrWhiteSpace(shellEvent.Description))
        {
            ElezenImgui.WrappedText(shellEvent.Description);
        }

        ImGui.Separator();
    }

    private void StartCommunityLoad()
    {
        _status = string.Empty;
        _communityLoadOperation.Reset();
        _ = _communityLoadOperation.Run(() => _apiController.GroupGetCommunity(new GroupDto(GroupFullInfo.Group)));
    }

    private void ConsumeCommunityLoad()
    {
        if (!_communityLoadOperation.IsCompleted)
        {
            return;
        }

        if (!_communityLoadOperation.Faulted)
        {
            _community = _communityLoadOperation.Result;
        }
        else
        {
            _status = _communityLoadOperation.Error ?? "Unable to load events.";
        }

        _communityLoadOperation.Reset();
    }

    private static string FormatStartsIn(TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero)
        {
            return "starting now";
        }

        if (delta.TotalMinutes < 1)
        {
            return "in less than a minute";
        }

        return "in " + FormatDuration(delta);
    }

    private static string FormatDuration(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalDays >= 1)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0}d {1}h", (int)delta.TotalDays, delta.Hours);
        }

        if (delta.TotalHours >= 1)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0}h {1}m", (int)delta.TotalHours, delta.Minutes);
        }

        return string.Format(CultureInfo.CurrentCulture, "{0} min", Math.Max(1, (int)delta.TotalMinutes));
    }
}
