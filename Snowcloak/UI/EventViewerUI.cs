using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class EventViewerUI : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly EventAggregator _eventAggregator;
    private readonly SnowcloakConfigService _configService;
    private List<EventViewRow> _currentEvents = [];
    private Lazy<List<EventViewRow>> _filteredEvents;
    private string _filterFreeText = string.Empty;
    private string _filterUid = string.Empty;
    private EventSeverity? _filterSeverity;
    private bool _isPaused;

    private List<EventViewRow> CurrentEvents
    {
        get
        {
            return _currentEvents;
        }
        set
        {
            _currentEvents = value;
            _filteredEvents = RecreateFilter();
        }
    }

    public EventViewerUI(ILogger<EventViewerUI> logger, SnowMediator mediator,
        EventAggregator eventAggregator, SnowcloakConfigService configService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Event Viewer###SnowcloakEventViewerUI", performanceCollectorService)
    {
        _eventAggregator = eventAggregator;
        _configService = configService;
        SetScaledSizeConstraints(new Vector2(700, 400));
        _filteredEvents = RecreateFilter();
        WindowName = "Event Viewer###SnowcloakEventViewerUI";
    }

    private Lazy<List<EventViewRow>> RecreateFilter()
    {
        return new(() =>
            CurrentEvents.Where(MatchesFilters).ToList());
    }

    private bool MatchesFilters(EventViewRow row)
    {
        if (_filterSeverity.HasValue && row.Severity != _filterSeverity.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_filterUid)
            && !row.UID.Contains(_filterUid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrEmpty(_filterFreeText)
               || row.FilterText.Contains(_filterFreeText, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearFilters()
    {
        _filterFreeText = string.Empty;
        _filterUid = string.Empty;
        _filterSeverity = null;
        _filteredEvents = RecreateFilter();
    }

    public override void OnOpen()
    {
        RefreshEvents();
        ClearFilters();
    }

    private void RefreshEvents()
    {
        CurrentEvents = _eventAggregator.EventList.Value.OrderByDescending(f => f.EventTime).Select(CreateViewRow).ToList();
    }

    private static EventViewRow CreateViewRow(Event ev)
    {
        var icon = ev.EventSeverity switch
        {
            EventSeverity.Informational => FontAwesomeIcon.InfoCircle,
            EventSeverity.Warning => FontAwesomeIcon.ExclamationTriangle,
            EventSeverity.Error => FontAwesomeIcon.Cross,
            _ => FontAwesomeIcon.QuestionCircle
        };

        Vector4? iconColor = ev.EventSeverity switch
        {
            EventSeverity.Warning => ImGuiColors.DalamudYellow,
            EventSeverity.Error => ImGuiColors.DalamudRed,
            _ => null
        };

        var filterText = string.Join('\n', ev.EventSource, ev.Character, ev.UID, ev.Message);
        return new EventViewRow(
            ev.EventTime.ToString("T", CultureInfo.CurrentCulture),
            ev.EventSource,
            ev.UID,
            ev.Character,
            ev.Message,
            ev.EventSeverity,
            ev.EventSeverity.ToString(),
            icon,
            iconColor,
            filterText);
    }

    protected override void DrawInternal()
    {
        var unfreezeLabel = "Unfreeze View";
        var freezeLabel ="Freeze View";
        var newEventsTooltip = "New events are available. Click to resume updating.";
        var filterLabel = "Filter lines";
        var uidFilterLabel = "UID";
        var severityFilterLabel = "Severity";
        var openFolderLabel = "Open EventLog folder";
        var timeColumnLabel = "Time";
        var sourceColumnLabel = "Source";
        var uidColumnLabel = "UID";
        var characterColumnLabel = "Character";
        var eventColumnLabel = "Event";
        var noValueLabel = "--";
        var newEventsAvailable = _eventAggregator.NewEventsAvailable;

        var freezeSize = ElezenImgui.GetIconButtonTextSize(FontAwesomeIcon.PlayCircle, unfreezeLabel);
        if (_isPaused)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, newEventsAvailable))
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.PlayCircle, unfreezeLabel))
                    _isPaused = false;
                if (newEventsAvailable)
                    ElezenImgui.AttachTooltip(newEventsTooltip);
            }
        }
        else
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.PauseCircle, freezeLabel))
                _isPaused = true;
        }

        if (newEventsAvailable && !_isPaused)
            RefreshEvents();

        ImGui.SameLine(freezeSize + ImGui.GetStyle().ItemSpacing.X * 2);

        bool changedFilter = false;
        ImGui.SetNextItemWidth(200);
        changedFilter |= ImGui.InputText(filterLabel, ref _filterFreeText, 50);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        changedFilter |= ImGui.InputText(uidFilterLabel, ref _filterUid, 40);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        changedFilter |= DrawSeverityFilter(severityFilterLabel);

        if (changedFilter) _filteredEvents = RecreateFilter();

        using (ImRaii.Disabled(!HasActiveFilters()))
        {
            ImGui.SameLine();
            if (ElezenImgui.IconButton(FontAwesomeIcon.Ban))
            {
                ClearFilters();
            }
        }

        if (_configService.Current.LogEvents)
        {
            var buttonSize = ElezenImgui.GetIconButtonTextSize(FontAwesomeIcon.FolderOpen, openFolderLabel);
            var dist = ImGui.GetWindowContentRegionMax().X - buttonSize;
            ImGui.SameLine(dist);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FolderOpen, openFolderLabel))
            {
                ProcessStartInfo ps = new()
                {
                    FileName = _eventAggregator.EventLogFolder,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                Process.Start(ps);
            }
        }

        var cursorPos = ImGui.GetCursorPosY();
        var max = ImGui.GetWindowContentRegionMax();
        var min = ImGui.GetWindowContentRegionMin();
        var width = max.X - min.X;
        var height = max.Y - cursorPos;
        using var table = ImRaii.Table("eventTable", 6, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg,
            new Vector2(width, height));

        float timeColWidth = ImGui.CalcTextSize("88:88:88 PM").X;
        float sourceColWidth = ImGui.CalcTextSize("PairManager").X;
        float uidColWidth = ImGui.CalcTextSize("WWWWWWW").X;
        float characterColWidth = ImGui.CalcTextSize("Wwwwww Wwwwww").X;

        if (table)
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.NoSort);
            ImGui.TableSetupColumn(timeColumnLabel, ImGuiTableColumnFlags.None, timeColWidth);
            ImGui.TableSetupColumn(sourceColumnLabel, ImGuiTableColumnFlags.None, sourceColWidth);
            ImGui.TableSetupColumn(uidColumnLabel, ImGuiTableColumnFlags.None, uidColWidth);
            ImGui.TableSetupColumn(characterColumnLabel, ImGuiTableColumnFlags.None, characterColWidth);
            ImGui.TableSetupColumn(eventColumnLabel, ImGuiTableColumnFlags.None);
            ImGui.TableHeadersRow();
            var events = _filteredEvents.Value;
            var clipper = new ImGuiListClipper();
            clipper.Begin(events.Count);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var ev = events[i];

                    ImGui.TableNextColumn();
                    using (ImRaii.PushColor(ImGuiCol.Text, ev.IconColor.GetValueOrDefault(), ev.IconColor.HasValue))
                    {
                        if (ElezenImgui.IconButton(ev.Icon))
                        {
                            _filterSeverity = ev.Severity;
                            _filteredEvents = RecreateFilter();
                        }
                    }
                    ElezenImgui.AttachTooltip(ev.SeverityText);
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(ev.TimeText);
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(ev.EventSource);
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    if (!string.IsNullOrEmpty(ev.UID))
                    {
                        if (ImGui.Selectable(ev.UID + $"##{i}"))
                        {
                            _filterUid = ev.UID;
                            _filteredEvents = RecreateFilter();
                        }
                    }
                    else
                    {
                        ImGui.TextUnformatted(noValueLabel);
                    }
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    if (!string.IsNullOrEmpty(ev.Character))
                    {
                        if (ImGui.Selectable(ev.Character + $"##{i}"))
                        {
                            _filterFreeText = ev.Character;
                            _filteredEvents = RecreateFilter();
                        }
                    }
                    else
                    {
                        ImGui.TextUnformatted(noValueLabel);
                    }
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    var posX = ImGui.GetCursorPosX();
                    var maxTextLength = ImGui.GetWindowContentRegionMax().X - posX;
                    var textSize = ImGui.CalcTextSize(ev.Message).X;
                    var msg = ev.Message;
                    while (textSize > maxTextLength)
                    {
                        msg = msg[..^5] + "...";
                        textSize = ImGui.CalcTextSize(msg).X;
                    }
                    ImGui.TextUnformatted(msg);
                    if (!string.Equals(msg, ev.Message, StringComparison.Ordinal))
                    {
                        ElezenImgui.AttachTooltip(ev.Message);
                    }
                }
            }
            clipper.End();
        }
    }

    private bool DrawSeverityFilter(string label)
    {
        var changed = false;
        var preview = _filterSeverity?.ToString() ?? "All severities";
        if (ImGui.BeginCombo(label, preview))
        {
            if (ImGui.Selectable("All severities", !_filterSeverity.HasValue))
            {
                _filterSeverity = null;
                changed = true;
            }

            foreach (var severity in Enum.GetValues<EventSeverity>())
            {
                if (ImGui.Selectable(severity.ToString(), _filterSeverity == severity))
                {
                    _filterSeverity = severity;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private bool HasActiveFilters()
    {
        return _filterSeverity.HasValue
               || !string.IsNullOrWhiteSpace(_filterFreeText)
               || !string.IsNullOrWhiteSpace(_filterUid);
    }

    private sealed record EventViewRow(
        string TimeText,
        string EventSource,
        string UID,
        string Character,
        string Message,
        EventSeverity Severity,
        string SeverityText,
        FontAwesomeIcon Icon,
        Vector4? IconColor,
        string FilterText);
}
