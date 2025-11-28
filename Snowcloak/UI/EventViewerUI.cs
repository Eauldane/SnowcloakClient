using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace Snowcloak.UI;

internal class EventViewerUI : WindowMediatorSubscriberBase
{
    private readonly EventAggregator _eventAggregator;
    private readonly UiSharedService _uiSharedService;
    private readonly SnowcloakConfigService _configService;
    private List<Event> _currentEvents = new();
    private Lazy<List<Event>> _filteredEvents;
    private string _filterFreeText = string.Empty;
    private bool _isPaused = false;

    private List<Event> CurrentEvents
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
        EventAggregator eventAggregator, UiSharedService uiSharedService, SnowcloakConfigService configService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Event Viewer", performanceCollectorService)
    {
        _eventAggregator = eventAggregator;
        _uiSharedService = uiSharedService;
        _configService = configService;
        SizeConstraints = new()
        {
            MinimumSize = new(700, 400)
        };
        _filteredEvents = RecreateFilter();
    }

    private Lazy<List<Event>> RecreateFilter()
    {
        return new(() =>
            CurrentEvents.Where(f =>
                string.IsNullOrEmpty(_filterFreeText)
                || (f.EventSource.Contains(_filterFreeText, StringComparison.OrdinalIgnoreCase)
                    || f.Character.Contains(_filterFreeText, StringComparison.OrdinalIgnoreCase)
                    || f.UID.Contains(_filterFreeText, StringComparison.OrdinalIgnoreCase)
                    || f.Message.Contains(_filterFreeText, StringComparison.OrdinalIgnoreCase)
                )
             ).ToList());
    }

    private void ClearFilters()
    {
        _filterFreeText = string.Empty;
        _filteredEvents = RecreateFilter();
    }

    public override void OnOpen()
    {
        CurrentEvents = _eventAggregator.EventList.Value.OrderByDescending(f => f.EventTime).ToList();
        ClearFilters();
    }

    protected override void DrawInternal()
    {
        var newEventsAvailable = _eventAggregator.NewEventsAvailable;

        var freezeSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.PlayCircle, "Unfreeze View");
        if (_isPaused)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, newEventsAvailable))
            {
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.PlayCircle, "Unfreeze View"))
                    _isPaused = false;
                if (newEventsAvailable)
                    UiSharedService.AttachToolTip("New events are available. Click to resume updating.");
            }
        }
        else
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.PauseCircle, "Freeze View"))
                _isPaused = true;
        }

        if (newEventsAvailable && !_isPaused)
            CurrentEvents = _eventAggregator.EventList.Value.OrderByDescending(f => f.EventTime).ToList();

        ImGui.SameLine(freezeSize + ImGui.GetStyle().ItemSpacing.X * 2);

        bool changedFilter = false;
        ImGui.SetNextItemWidth(200);
        changedFilter |= ImGui.InputText("Filter lines", ref _filterFreeText, 50);
        if (changedFilter) _filteredEvents = RecreateFilter();

        using (ImRaii.Disabled(_filterFreeText.IsNullOrEmpty()))
        {
            ImGui.SameLine();
            if (_uiSharedService.IconButton(FontAwesomeIcon.Ban))
            {
                _filterFreeText = string.Empty;
                _filteredEvents = RecreateFilter();
            }
        }

        if (_configService.Current.LogEvents)
        {
            var buttonSize = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.FolderOpen, "Open EventLog Folder");
            var dist = ImGui.GetWindowContentRegionMax().X - buttonSize;
            ImGui.SameLine(dist);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.FolderOpen, "Open EventLog folder"))
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
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.None, timeColWidth);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.None, sourceColWidth);
            ImGui.TableSetupColumn("UID", ImGuiTableColumnFlags.None, uidColWidth);
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.None, characterColWidth);
            ImGui.TableSetupColumn("Event", ImGuiTableColumnFlags.None);
            ImGui.TableHeadersRow();
            int i = 0;
            foreach (var ev in _filteredEvents.Value)
            {
                ++i;

                var icon = ev.EventSeverity switch
                {
                    EventSeverity.Informational => FontAwesomeIcon.InfoCircle,
                    EventSeverity.Warning => FontAwesomeIcon.ExclamationTriangle,
                    EventSeverity.Error => FontAwesomeIcon.Cross,
                    _ => FontAwesomeIcon.QuestionCircle
                };

                var iconColor = ev.EventSeverity switch
                {
                    EventSeverity.Informational => new Vector4(),
                    EventSeverity.Warning => ImGuiColors.DalamudYellow,
                    EventSeverity.Error => ImGuiColors.DalamudRed,
                    _ => new Vector4()
                };

                ImGui.TableNextColumn();
                _uiSharedService.IconText(icon, iconColor == new Vector4() ? null : iconColor);
                UiSharedService.AttachToolTip(ev.EventSeverity.ToString());
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(ev.EventTime.ToString("T", CultureInfo.CurrentCulture));
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(ev.EventSource);
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                if (!string.IsNullOrEmpty(ev.UID))
                {
                    if (ImGui.Selectable(ev.UID + $"##{i}"))
                    {
                        _filterFreeText = ev.UID;
                        _filteredEvents = RecreateFilter();
                    }
                }
                else
                {
                    ImGui.TextUnformatted("--");
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
                    ImGui.TextUnformatted("--");
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
                    UiSharedService.AttachToolTip(ev.Message);
                }
            }
        }
    }
}
