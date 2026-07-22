using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.Roleplay;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;

namespace Snowcloak.UI.Components;

internal sealed class SyncshellCommunityManagementPanel
{
    private static readonly GroupDirectoryJoinPolicy[] JoinPolicies =
    [
        GroupDirectoryJoinPolicy.Open,
        GroupDirectoryJoinPolicy.Request,
        GroupDirectoryJoinPolicy.InviteOnly
    ];

    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SnowMediator _mediator;
    private readonly AsyncOp<GroupCommunityDto> _communityLoadOperation = new();
    private readonly AsyncOp<GroupCommunityDto> _motdSaveOperation = new();
    private readonly AsyncOp<GroupCommunityDto> _eventSaveOperation = new();
    private readonly AsyncOp<GroupEventDigestConfigDto> _digestOperation = new();
    private readonly AsyncOp<GroupDirectoryListingDto> _listingLoadOperation = new();
    private readonly AsyncOp<GroupDirectoryListingDto> _listingSaveOperation = new();
    private readonly Dictionary<Guid, AsyncOp<GroupCommunityDto>> _eventDeleteOperations = [];
    private string _activeGid = string.Empty;
    private GroupCommunityDto? _community;
    private GroupDirectoryListingDto? _listing;
    private string _motdDraft = string.Empty;
    private string _communitySection = "Overview";
    private string _eventSection = "Schedule";
    private Guid _eventId;
    private string _eventTitleDraft = string.Empty;
    private string _eventDescriptionDraft = string.Empty;
    private string _eventStartDraft = CreateDefaultEventStart();
    private int _eventDurationMinutes = 120;
    private bool _eventReminderEnabled = true;
    private string _eventPlotDraft = string.Empty;
    private string _eventWorldDraft = string.Empty;
    private string _eventHostsDraft = string.Empty;
    private string _eventTagsDraft = string.Empty;
    private string _eventWarningsDraft = string.Empty;
    private int _eventCapacityDraft;
    private string _eventBannerDraft = string.Empty;
    private bool _eventPublicDraft;
    private ProfileContentRating _eventRatingDraft;
    private bool _eventRecurringDraft;
    private RpEventRecurrenceFrequency _eventFrequencyDraft = RpEventRecurrenceFrequency.Weekly;
    private int _eventIntervalDraft = 1;
    private int _eventDaysMaskDraft;
    private string _eventUntilDraft = string.Empty;
    private int _eventOccurrenceCountDraft;
    private bool _digestLoaded;
    private bool _digestSavePending;
    private bool _digestEnabled;
    private string _digestWebhookDraft = string.Empty;
    private int _digestMinuteUtc = 720;
    private string _listingDescriptionDraft = string.Empty;
    private string _listingTagsDraft = string.Empty;
    private ushort _mainWorldDraft;
    private string _mainRegionDraft = string.Empty;
    private string _communityStatus = string.Empty;
    private Vector4 _communityStatusColour = ImGuiColors.DalamudYellow;
    private string _listingStatus = string.Empty;
    private Vector4 _listingStatusColour = ImGuiColors.DalamudYellow;

    public SyncshellCommunityManagementPanel(ApiController apiController, DalamudUtilService dalamudUtilService, SnowMediator mediator)
    {
        _apiController = apiController;
        _dalamudUtilService = dalamudUtilService;
        _mediator = mediator;
    }

    public void DrawCommunity(GroupFullInfoDto group)
    {
        EnsureGroup(group);
        ConsumeCommunityOperations();
        ConsumeListingOperations();

        ModernSection.Header(FontAwesomeIcon.Bullhorn, "Community");
        _communitySection = ModernTabBar.Draw("syncshell-community-tabs", ["Overview", "Directory listing"], _communitySection);
        ImGuiHelpers.ScaledDummy(6f);

        if (string.Equals(_communitySection, "Overview", StringComparison.Ordinal))
        {
            DrawCommunityOverview(group);
        }
        else
        {
            DrawDirectoryListing(group);
        }
    }

    private void DrawCommunityOverview(GroupFullInfoDto group)
    {
        EnsureCommunityLoaded(group);

        using (ImRaii.Disabled(_communityLoadOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Retweet, "Refresh community"))
            {
                StartCommunityLoad(group);
            }
        }
        DrawOperationStatus(_communityLoadOperation, "Refreshing...");
        DrawStatus(_communityStatus, _communityStatusColour);

        var community = _community;
        if (community == null)
        {
            if (_communityLoadOperation.IsRunning)
            {
                ElezenImgui.ColouredWrappedText("Loading community details...", ImGuiColors.DalamudYellow);
            }

            return;
        }

        ImGuiHelpers.ScaledDummy(2f);
        DrawMotdEditor(group);
    }

    public void DrawEvents(GroupFullInfoDto group)
    {
        EnsureGroup(group);
        ConsumeCommunityOperations();
        ConsumeDigestOperation();
        EnsureCommunityLoaded(group);
        EnsureDigestLoaded(group);

        DrawEventsHeader(group);
        DrawOperationStatus(_communityLoadOperation, "Refreshing...");
        DrawStatus(_communityStatus, _communityStatusColour);

        _eventSection = ModernTabBar.Draw("syncshell-event-admin-tabs", ["Schedule", "Event editor", "Discord digest"], _eventSection);
        ImGuiHelpers.ScaledDummy(6f);
        if (string.Equals(_eventSection, "Schedule", StringComparison.Ordinal))
        {
            if (_community == null)
            {
                ElezenImgui.ColouredWrappedText("Loading event schedule...", ImGuiColors.DalamudYellow);
                return;
            }

            DrawEventList(group, _community);
        }
        else if (string.Equals(_eventSection, "Event editor", StringComparison.Ordinal))
        {
            DrawEventEditor(group);
        }
        else
        {
            DrawDigestEditor(group);
        }
    }

    private void DrawEventsHeader(GroupFullInfoDto group)
    {
        var start = ImGui.GetCursorPos();
        ModernSection.Header(FontAwesomeIcon.CalendarDay, "Roleplay events");
        var headerBottom = ImGui.GetCursorPosY();
        var buttonWidth = ElezenImgui.GetIconButtonTextSize(FontAwesomeIcon.Retweet, "Refresh events");
        ImGui.SetCursorPos(new Vector2(
            MathF.Max(start.X, ImGui.GetWindowContentRegionMax().X - buttonWidth),
            start.Y));

        using (ImRaii.Disabled(_communityLoadOperation.IsRunning || _digestOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Retweet, "Refresh events"))
            {
                StartCommunityLoad(group);
                StartDigestLoad(group);
            }
        }

        ImGui.SetCursorPos(new Vector2(start.X, MathF.Max(headerBottom, ImGui.GetCursorPosY())));
    }

    private void DrawDirectoryListing(GroupFullInfoDto group)
    {
        EnsureListingLoaded(group);

        using (ImRaii.Disabled(_listingLoadOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Retweet, "Refresh directory listing"))
            {
                StartListingLoad(group);
            }
        }
        DrawOperationStatus(_listingLoadOperation, "Refreshing...");
        DrawStatus(_listingStatus, _listingStatusColour);

        var listing = _listing;
        if (listing == null)
        {
            if (_listingLoadOperation.IsRunning)
            {
                ElezenImgui.ColouredWrappedText("Loading directory listing...", ImGuiColors.DalamudYellow);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(listing.ErrorMessage))
        {
            ElezenImgui.ColouredWrappedText(listing.ErrorMessage, ImGuiColors.DalamudRed);
            return;
        }

        ImGuiHelpers.ScaledDummy(2f);
        DrawDirectoryEditor(listing);
    }

    private void EnsureGroup(GroupFullInfoDto group)
    {
        if (string.Equals(_activeGid, group.GID, StringComparison.Ordinal))
        {
            return;
        }

        _activeGid = group.GID;
        _community = null;
        _listing = null;
        _motdDraft = string.Empty;
        _communitySection = "Overview";
        ResetEventDraft();
        _eventSection = "Schedule";
        _digestLoaded = false;
        _digestSavePending = false;
        _digestEnabled = false;
        _digestWebhookDraft = string.Empty;
        _digestMinuteUtc = 720;
        _listingDescriptionDraft = string.Empty;
        _listingTagsDraft = string.Empty;
        _mainWorldDraft = 0;
        _mainRegionDraft = string.Empty;
        _communityStatus = string.Empty;
        _listingStatus = string.Empty;
        _communityLoadOperation.Reset();
        _motdSaveOperation.Reset();
        _eventSaveOperation.Reset();
        _digestOperation.Reset();
        _listingLoadOperation.Reset();
        _listingSaveOperation.Reset();
        _eventDeleteOperations.Clear();
    }

    private void DrawMotdEditor(GroupFullInfoDto group)
    {
        ImGui.TextUnformatted("Message of the day");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##motd", ref _motdDraft, 2000, new Vector2(-1, 96f * ImGuiHelpers.GlobalScale));

        using (ImRaii.Disabled(_motdSaveOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save MOTD"))
            {
                var motd = string.IsNullOrWhiteSpace(_motdDraft) ? null : _motdDraft.Trim();
                _communityStatus = string.Empty;
                _ = _motdSaveOperation.Run(() => _apiController.GroupSetMotd(new GroupMotdUpdateDto(group.Group) { Motd = motd }));
            }
        }
        DrawOperationStatus(_motdSaveOperation, "Saving...");
    }

    private void DrawEventList(GroupFullInfoDto group, GroupCommunityDto community)
    {
        ModernSection.Header(FontAwesomeIcon.Calendar, "Schedule");
        ImGuiHelpers.ScaledDummy(4f);
        if (community.Events.Count == 0)
        {
            DrawEmptyEventSchedule();
            return;
        }

        foreach (var shellEvent in community.Events.OrderBy(e => e.StartsAtUtc))
        {
            DrawEventRow(group, shellEvent);
        }
    }

    private void DrawEmptyEventSchedule()
    {
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanelAlt);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f) * ImGuiHelpers.GlobalScale);
        using var card = ImRaii.Child("admin-event-schedule-empty", new Vector2(-1f, 132f * ImGuiHelpers.GlobalScale), true);
        if (!card)
        {
            return;
        }

        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(SnowcloakColours.OnlineBlue, FontAwesomeIcon.CalendarPlus.ToIconString());
        ImGui.SameLine(0f, 10f * ImGuiHelpers.GlobalScale);
        ImGui.BeginGroup();
        ImGui.TextUnformatted("No events scheduled");
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            "Create an event to publish it to your members and, if enabled, the RP directory.");
        ImGuiHelpers.ScaledDummy(4f);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Create event"))
        {
            ResetEventDraft();
            _eventSection = "Event editor";
        }
        ImGui.EndGroup();
    }

    private void DrawEventRow(GroupFullInfoDto group, GroupEventDto shellEvent)
    {
        using var id = ImRaii.PushId("admin-event-" + shellEvent.Id.ToString("N"));
        var details = new List<string>();
        if (shellEvent.EndsAtUtc.HasValue)
            details.Add("Ends " + shellEvent.EndsAtUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        if (shellEvent.IsPublic)
            details.Add("Public");
        if (shellEvent.Recurrence != null)
            details.Add(FormatRecurrence(shellEvent.Recurrence));
        if (shellEvent.ContentRating != ProfileContentRating.General)
            details.Add(shellEvent.ContentRating.ToString());
        var descriptionLines = string.IsNullOrWhiteSpace(shellEvent.Description)
            ? []
            : FrostbrandPanelChrome.WrapText(shellEvent.Description,
                MathF.Max(80f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - 28f * ImGuiHelpers.GlobalScale));
        var extraLines = (details.Count > 0 ? 1 : 0)
            + (shellEvent.ThemeTags.Count > 0 ? 1 : 0)
            + (shellEvent.ContentWarnings.Count > 0 ? 1 : 0);
        var lineStep = ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y;
        var height = MathF.Max(72f * ImGuiHelpers.GlobalScale,
            24f * ImGuiHelpers.GlobalScale + (1 + descriptionLines.Count + extraLines) * lineStep);

        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanelAlt);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12f, 9f) * ImGuiHelpers.GlobalScale);
        using var card = ImRaii.Child("event-card", new Vector2(-1f, height), true);
        if (!card)
        {
            return;
        }

        ImGui.AlignTextToFramePadding();
        ElezenImgui.ShowIcon(FontAwesomeIcon.Calendar, SnowcloakColours.OnlineBlue);
        ImGui.SameLine();
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "{0:g}  {1}", shellEvent.StartsAtUtc.ToLocalTime(), shellEvent.Title));

        var deleteOperation = GetEventDeleteOperation(shellEvent.Id);
        ImGui.SameLine();
        if (ImGui.SmallButton("Edit"))
        {
            LoadEventDraft(shellEvent);
            _eventSection = "Event editor";
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(deleteOperation.IsRunning))
        {
            if (ElezenImgui.IconButton(FontAwesomeIcon.Trash))
            {
                _communityStatus = string.Empty;
                _ = deleteOperation.Run(() => _apiController.GroupDeleteEvent(new GroupEventDeleteDto(group.Group, shellEvent.Id)));
            }
        }
        ElezenImgui.AttachTooltip("Delete event");
        DrawOperationStatus(deleteOperation, "Deleting...");

        foreach (var line in descriptionLines)
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, line);
        if (details.Count > 0)
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, string.Join("  |  ", details));
        if (shellEvent.ThemeTags.Count > 0)
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, string.Join("   ", shellEvent.ThemeTags.Select(tag => "#" + tag)));
        if (shellEvent.ContentWarnings.Count > 0)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Warnings: " + string.Join(", ", shellEvent.ContentWarnings));
        ImGuiHelpers.ScaledDummy(6f);
    }

    private void DrawEventEditor(GroupFullInfoDto group)
    {
        ModernSection.Header(FontAwesomeIcon.CalendarPlus, _eventId == Guid.Empty ? "Create event" : "Edit event");
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##eventtitle", "Event title", ref _eventTitleDraft, 100);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##eventdescription", ref _eventDescriptionDraft, 1000, new Vector2(-1f, 70f * ImGuiHelpers.GlobalScale));
        ImGui.SetNextItemWidth(210f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##eventstart", "Local start date and time", ref _eventStartDraft, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Duration (min)", ref _eventDurationMinutes, 15, 60);
        _eventDurationMinutes = Math.Clamp(_eventDurationMinutes, 15, 10080);

        ModernSection.SoftSeparator();
        ModernSection.Header(FontAwesomeIcon.MapMarkerAlt, "Location and attendance");
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##eventplot", "Plot / location", ref _eventPlotDraft, 100);
        DrawEventWorldPicker();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Attendance limit", ref _eventCapacityDraft);
        _eventCapacityDraft = Math.Max(0, _eventCapacityDraft);
        ElezenImgui.AttachTooltip("Optional. Leave this at 0 when there is no limit on the number of attendees.");
        ImGui.SameLine();
        ImGui.Checkbox("Built-in reminder", ref _eventReminderEnabled);
        ElezenImgui.AttachTooltip("Send attendees a Snowcloak reminder before the event begins.");

        ModernSection.SoftSeparator();
        ModernSection.Header(FontAwesomeIcon.Globe, "Directory and recurrence");
        ImGuiHelpers.ScaledDummy(4f);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##eventhosts", "Hosts, comma-separated", ref _eventHostsDraft, 300);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##eventtags", "Theme tags, comma-separated", ref _eventTagsDraft, 512);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##eventwarnings", "Content warnings, comma-separated", ref _eventWarningsDraft, 512);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##eventbanner", "Optional banner image hash", ref _eventBannerDraft, 160);

        ImGui.Checkbox("Public RP directory listing", ref _eventPublicDraft);
        ElezenImgui.AttachTooltip("Public events allow eligible players to join this syncshell without a password.");
        ImGui.SameLine();
        DrawEnumCombo("##eventrating", ref _eventRatingDraft);
        ImGui.SameLine();
        ImGui.Checkbox("Recurring", ref _eventRecurringDraft);
        if (_eventRecurringDraft)
        {
            DrawEnumCombo("##eventfrequency", ref _eventFrequencyDraft);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
            ImGui.InputInt("Interval", ref _eventIntervalDraft);
            _eventIntervalDraft = Math.Clamp(_eventIntervalDraft, 1, 52);
            if (_eventFrequencyDraft == RpEventRecurrenceFrequency.Weekly)
            {
                string[] days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
                foreach (var (day, index) in days.Select((day, index) => (day, index)))
                {
                    var selected = (_eventDaysMaskDraft & (1 << index)) != 0;
                    if (ImGui.Checkbox(day + "##admin-event-day", ref selected))
                    {
                        if (selected) _eventDaysMaskDraft |= 1 << index;
                        else _eventDaysMaskDraft &= ~(1 << index);
                    }
                    if (index < days.Length - 1) ImGui.SameLine();
                }
            }
            ImGui.SetNextItemWidth(210f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("##eventuntil", "Optional local end date", ref _eventUntilDraft, 64);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(130f * ImGuiHelpers.GlobalScale);
            ImGui.InputInt("Occurrences", ref _eventOccurrenceCountDraft);
            _eventOccurrenceCountDraft = Math.Clamp(_eventOccurrenceCountDraft, 0, 365);
        }

        var hasValidStart = DateTime.TryParse(_eventStartDraft, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsedStart);
        var hasValidUntil = !_eventRecurringDraft
                            || string.IsNullOrWhiteSpace(_eventUntilDraft)
                            || DateTime.TryParse(_eventUntilDraft, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsedUntil)
                            && parsedUntil > parsedStart;
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_eventTitleDraft) || !hasValidStart || !hasValidUntil || _eventSaveOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, _eventId == Guid.Empty ? "Create event" : "Save event") && hasValidStart)
            {
                SaveEvent(group, parsedStart);
            }
        }
        DrawOperationStatus(_eventSaveOperation, "Saving...");
        if (_eventId != Guid.Empty)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel edit")) ResetEventDraft();
        }
        if (!hasValidStart)
            ElezenImgui.ColouredWrappedText("Enter a valid local start date and time.", ImGuiColors.DalamudYellow);
        else if (!hasValidUntil)
            ElezenImgui.ColouredWrappedText("Enter a valid local recurrence end date.", ImGuiColors.DalamudYellow);
    }

    private void DrawEventWorldPicker()
    {
        var selectedWorld = uint.TryParse(_eventWorldDraft, NumberStyles.None, CultureInfo.InvariantCulture, out var worldId)
            ? worldId
            : 0;
        var selectedWorldName = selectedWorld == 0
            ? "Any world"
            : _dalamudUtilService.GetWorldName(selectedWorld) ?? "Any world";

        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("World", selectedWorldName))
        {
            if (ImGui.Selectable("Any world", selectedWorld == 0))
            {
                _eventWorldDraft = string.Empty;
            }

            foreach (var region in _dalamudUtilService.WorldRegions)
            {
                ImGui.TextColored(SnowcloakColours.CompactTextMuted, region);
                foreach (var world in _dalamudUtilService.GetWorldsInRegion(region))
                {
                    if (ImGui.Selectable(world.Name, world.Id == selectedWorld))
                    {
                        _eventWorldDraft = world.Id.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }

            ImGui.EndCombo();
        }
        ElezenImgui.AttachTooltip("Optionally narrow the event location to a world, or leave it open to every world.");
    }

    private void DrawDigestEditor(GroupFullInfoDto group)
    {
        ModernSection.Header(FontAwesomeIcon.Bell, "Daily Discord digest");
        if (!_digestLoaded && _digestOperation.IsRunning)
        {
            ElezenImgui.ColouredWrappedText("Loading digest settings...", ImGuiColors.DalamudYellow);
            return;
        }

        ImGui.Checkbox("Enabled##admin-digest", ref _digestEnabled);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##admin-digest-webhook", "Discord webhook URL", ref _digestWebhookDraft, 500);
        var digestHourUtc = _digestMinuteUtc / 60;
        var digestMinuteUtc = _digestMinuteUtc % 60;
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        var timeChanged = ImGui.InputInt("UTC hour", ref digestHourUtc);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        timeChanged |= ImGui.InputInt("UTC minute", ref digestMinuteUtc, 5, 15);
        if (timeChanged)
            _digestMinuteUtc = Math.Clamp(digestHourUtc, 0, 23) * 60 + Math.Clamp(digestMinuteUtc, 0, 59);
        var utcTime = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(_digestMinuteUtc));
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            string.Format(CultureInfo.CurrentCulture, "Daily delivery time: {0:HH\\:mm} UTC", utcTime));
        using (ImRaii.Disabled(_digestOperation.IsRunning || _digestEnabled && string.IsNullOrWhiteSpace(_digestWebhookDraft)))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save digest"))
            {
                _digestSavePending = true;
                _ = _digestOperation.Run(() => _apiController.RpEventDigestSet(new GroupEventDigestConfigDto
                {
                    Group = group.Group,
                    Enabled = _digestEnabled,
                    WebhookUrl = string.IsNullOrWhiteSpace(_digestWebhookDraft) ? null : _digestWebhookDraft.Trim(),
                    MinuteOfDayUtc = _digestMinuteUtc,
                }));
            }
        }
        DrawOperationStatus(_digestOperation, "Saving...");
    }

    private void SaveEvent(GroupFullInfoDto group, DateTime parsedStart)
    {
        var startsAtUtc = parsedStart.ToUniversalTime();
        var untilUtc = DateTime.TryParse(_eventUntilDraft, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsedUntil)
            ? parsedUntil.ToUniversalTime()
            : (DateTime?)null;
        var worldId = uint.TryParse(_eventWorldDraft, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedWorld) && parsedWorld > 0
            ? parsedWorld
            : (uint?)null;
        var shellEvent = new GroupEventDto(_eventId == Guid.Empty ? Guid.NewGuid() : _eventId, _eventTitleDraft.Trim(), startsAtUtc)
        {
            Description = string.IsNullOrWhiteSpace(_eventDescriptionDraft) ? null : _eventDescriptionDraft.Trim(),
            EndsAtUtc = startsAtUtc.AddMinutes(_eventDurationMinutes),
            ReminderEnabled = _eventReminderEnabled,
            Plot = string.IsNullOrWhiteSpace(_eventPlotDraft) ? null : _eventPlotDraft.Trim(),
            WorldId = worldId,
            Hosts = ParseEventValues(_eventHostsDraft),
            ThemeTags = ParseEventValues(_eventTagsDraft),
            ContentWarnings = ParseEventValues(_eventWarningsDraft),
            Capacity = _eventCapacityDraft > 0 ? _eventCapacityDraft : null,
            BannerImageHash = string.IsNullOrWhiteSpace(_eventBannerDraft) ? null : _eventBannerDraft.Trim(),
            IsPublic = _eventPublicDraft,
            ContentRating = _eventRatingDraft,
            Recurrence = _eventRecurringDraft ? new GroupEventRecurrenceDto
            {
                Frequency = _eventFrequencyDraft,
                Interval = _eventIntervalDraft,
                DaysOfWeekMask = _eventFrequencyDraft == RpEventRecurrenceFrequency.Weekly ? _eventDaysMaskDraft : 0,
                UntilUtc = untilUtc,
                OccurrenceCount = _eventOccurrenceCountDraft > 0 ? _eventOccurrenceCountDraft : null,
            } : null,
        };
        _communityStatus = string.Empty;
        _ = _eventSaveOperation.Run(() => _apiController.GroupUpsertEvent(new GroupEventUpsertDto(group.Group, shellEvent)));
    }

    private void LoadEventDraft(GroupEventDto shellEvent)
    {
        _eventId = shellEvent.Id;
        _eventTitleDraft = shellEvent.Title;
        _eventDescriptionDraft = shellEvent.Description ?? string.Empty;
        _eventStartDraft = shellEvent.StartsAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        _eventDurationMinutes = Math.Max(15, (int)(shellEvent.EndsAtUtc.GetValueOrDefault(shellEvent.StartsAtUtc.AddHours(2)) - shellEvent.StartsAtUtc).TotalMinutes);
        _eventReminderEnabled = shellEvent.ReminderEnabled;
        _eventPlotDraft = shellEvent.Plot ?? string.Empty;
        _eventWorldDraft = shellEvent.WorldId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _eventHostsDraft = string.Join(", ", shellEvent.Hosts);
        _eventTagsDraft = string.Join(", ", shellEvent.ThemeTags);
        _eventWarningsDraft = string.Join(", ", shellEvent.ContentWarnings);
        _eventCapacityDraft = shellEvent.Capacity ?? 0;
        _eventBannerDraft = shellEvent.BannerImageHash ?? string.Empty;
        _eventPublicDraft = shellEvent.IsPublic;
        _eventRatingDraft = shellEvent.ContentRating;
        _eventRecurringDraft = shellEvent.Recurrence != null;
        _eventFrequencyDraft = shellEvent.Recurrence?.Frequency ?? RpEventRecurrenceFrequency.Weekly;
        _eventIntervalDraft = shellEvent.Recurrence?.Interval ?? 1;
        _eventDaysMaskDraft = shellEvent.Recurrence?.DaysOfWeekMask ?? 0;
        _eventUntilDraft = shellEvent.Recurrence?.UntilUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;
        _eventOccurrenceCountDraft = shellEvent.Recurrence?.OccurrenceCount ?? 0;
    }

    private void ResetEventDraft()
    {
        _eventId = Guid.Empty;
        _eventTitleDraft = string.Empty;
        _eventDescriptionDraft = string.Empty;
        _eventStartDraft = CreateDefaultEventStart();
        _eventDurationMinutes = 120;
        _eventReminderEnabled = true;
        _eventPlotDraft = string.Empty;
        _eventWorldDraft = string.Empty;
        _eventHostsDraft = string.Empty;
        _eventTagsDraft = string.Empty;
        _eventWarningsDraft = string.Empty;
        _eventCapacityDraft = 0;
        _eventBannerDraft = string.Empty;
        _eventPublicDraft = false;
        _eventRatingDraft = ProfileContentRating.General;
        _eventRecurringDraft = false;
        _eventFrequencyDraft = RpEventRecurrenceFrequency.Weekly;
        _eventIntervalDraft = 1;
        _eventDaysMaskDraft = 0;
        _eventUntilDraft = string.Empty;
        _eventOccurrenceCountDraft = 0;
    }

    private static string FormatRecurrence(GroupEventRecurrenceDto recurrence)
    {
        var interval = recurrence.Interval <= 1
            ? recurrence.Frequency.ToString()
            : recurrence.Frequency switch
            {
                RpEventRecurrenceFrequency.Daily => $"Every {recurrence.Interval} days",
                RpEventRecurrenceFrequency.Weekly => $"Every {recurrence.Interval} weeks",
                RpEventRecurrenceFrequency.Monthly => $"Every {recurrence.Interval} months",
                _ => recurrence.Frequency.ToString(),
            };
        if (recurrence.Frequency == RpEventRecurrenceFrequency.Weekly && recurrence.DaysOfWeekMask != 0)
        {
            string[] days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
            interval += " | " + string.Join(", ", days.Where((_, index) => (recurrence.DaysOfWeekMask & (1 << index)) != 0));
        }
        if (recurrence.OccurrenceCount.HasValue)
            interval += $" | {recurrence.OccurrenceCount.Value} occurrences";
        if (recurrence.UntilUtc.HasValue)
            interval += " | until " + recurrence.UntilUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return interval;
    }

    private static void DrawEnumCombo<T>(string id, ref T value) where T : struct, Enum
    {
        ImGui.SetNextItemWidth(130f * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginCombo(id, value.ToString())) return;
        foreach (var item in Enum.GetValues<T>())
        {
            if (ImGui.Selectable(item.ToString(), EqualityComparer<T>.Default.Equals(item, value)))
                value = item;
        }
        ImGui.EndCombo();
    }

    private void DrawDirectoryEditor(GroupDirectoryListingDto listing)
    {
        var listed = listing.IsListed;
        if (ImGui.Checkbox("Listed in community directory", ref listed))
        {
            listing.IsListed = listed;
        }

        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Join policy", FormatJoinPolicy(listing.JoinPolicy)))
        {
            foreach (var policy in JoinPolicies)
            {
                if (ImGui.Selectable(FormatJoinPolicy(policy), listing.JoinPolicy == policy))
                {
                    listing.JoinPolicy = policy;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##listingdescription", "Directory description", ref _listingDescriptionDraft, 1000);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##listingtags", "Tags separated by commas", ref _listingTagsDraft, 255);

        DrawLocationPicker();

        var missingRegion = listing.IsListed && string.IsNullOrEmpty(_mainRegionDraft);
        using (ImRaii.Disabled(_listingSaveOperation.IsRunning || missingRegion))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Save directory listing"))
            {
                listing.Description = string.IsNullOrWhiteSpace(_listingDescriptionDraft) ? null : _listingDescriptionDraft.Trim();
                listing.Tags = ParseTags(_listingTagsDraft);
                // A specific world implies its region; otherwise the chosen region stands alone.
                var region = _mainWorldDraft != 0
                    ? _dalamudUtilService.GetWorldRegion(_mainWorldDraft) ?? _mainRegionDraft
                    : _mainRegionDraft;
                listing.MainRegion = string.IsNullOrEmpty(region) ? null : region;
                listing.MainWorldId = _mainWorldDraft == 0 ? null : _mainWorldDraft;
                _listingStatus = string.Empty;
                _ = _listingSaveOperation.Run(() => _apiController.GroupDirectorySetListing(listing));
            }
        }
        DrawOperationStatus(_listingSaveOperation, "Saving...");
        if (missingRegion)
        {
            ElezenImgui.ColouredWrappedText("Choose a main region before listing this syncshell in the community directory.", ImGuiColors.DalamudYellow);
        }

        if (listing.IsListed && !listing.IsApproved)
        {
            ElezenImgui.ColouredWrappedText("This listing is saved but not currently approved for the community directory.", ImGuiColors.DalamudYellow);
        }
    }

    private void DrawLocationPicker()
    {
        // A syncshell's location is a region at the widest; it may optionally pin to one world.
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        var regionLabel = string.IsNullOrEmpty(_mainRegionDraft) ? "Select a region..." : _mainRegionDraft;
        if (ImGui.BeginCombo("Main region", regionLabel))
        {
            foreach (var region in _dalamudUtilService.WorldRegions)
            {
                if (ImGui.Selectable(region, string.Equals(region, _mainRegionDraft, StringComparison.Ordinal)))
                {
                    _mainRegionDraft = region;
                    _mainWorldDraft = 0; // region changed; drop any world that belonged to the old one
                }
            }

            ImGui.EndCombo();
        }
        ElezenImgui.AttachTooltip("The datacenter region this syncshell is based in. Required to list it in the community directory.");

        using (ImRaii.Disabled(string.IsNullOrEmpty(_mainRegionDraft)))
        {
            ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
            var worldLabel = _mainWorldDraft == 0 ? "Entire region" : _dalamudUtilService.GetWorldName(_mainWorldDraft) ?? "Entire region";
            if (ImGui.BeginCombo("Main world", worldLabel))
            {
                if (ImGui.Selectable("Entire region", _mainWorldDraft == 0))
                {
                    _mainWorldDraft = 0;
                }

                if (!string.IsNullOrEmpty(_mainRegionDraft))
                {
                    foreach (var world in _dalamudUtilService.GetWorldsInRegion(_mainRegionDraft))
                    {
                        if (ImGui.Selectable(world.Name, world.Id == _mainWorldDraft))
                        {
                            _mainWorldDraft = world.Id;
                        }
                    }
                }

                ImGui.EndCombo();
            }
        }
        ElezenImgui.AttachTooltip("Optionally narrow the location to a single world, or leave it covering the entire region.");
    }

    private void EnsureCommunityLoaded(GroupFullInfoDto group)
    {
        if (_community != null || _communityLoadOperation.IsRunning || !string.IsNullOrWhiteSpace(_communityStatus))
        {
            return;
        }

        StartCommunityLoad(group);
    }

    private void EnsureListingLoaded(GroupFullInfoDto group)
    {
        if (_listing != null || _listingLoadOperation.IsRunning || !string.IsNullOrWhiteSpace(_listingStatus))
        {
            return;
        }

        StartListingLoad(group);
    }

    private void EnsureDigestLoaded(GroupFullInfoDto group)
    {
        if (_digestLoaded || _digestOperation.IsRunning)
            return;
        StartDigestLoad(group);
    }

    private void StartCommunityLoad(GroupFullInfoDto group)
    {
        _communityStatus = string.Empty;
        _communityLoadOperation.Reset();
        _ = _communityLoadOperation.Run(() => _apiController.GroupGetCommunity(new GroupDto(group.Group)));
    }

    private void StartListingLoad(GroupFullInfoDto group)
    {
        _listingStatus = string.Empty;
        _listingLoadOperation.Reset();
        _ = _listingLoadOperation.Run(() => _apiController.GroupDirectoryGetOwn(new GroupDto(group.Group)));
    }

    private void StartDigestLoad(GroupFullInfoDto group)
    {
        _digestLoaded = false;
        _digestSavePending = false;
        _digestOperation.Reset();
        _ = _digestOperation.Run(() => _apiController.RpEventDigestGet(new GroupDto(group.Group)));
    }

    private void ConsumeCommunityOperations()
    {
        ConsumeCommunityResult(_communityLoadOperation, "Unable to load community details.");
        ConsumeCommunityResult(_motdSaveOperation, "Unable to save MOTD.", "MOTD saved.");

        if (ConsumeCommunityResult(_eventSaveOperation, "Unable to save event.", "Event schedule updated."))
        {
            ResetEventDraft();
            _eventSection = "Schedule";
        }

        foreach (var entry in _eventDeleteOperations.Where(kvp => kvp.Value.IsCompleted).ToList())
        {
            var operation = entry.Value;
            if (operation.Faulted)
            {
                SetCommunityStatus(operation.Error ?? "Unable to delete event.", ImGuiColors.DalamudRed);
            }
            else
            {
                ApplyCommunity(operation.Result);
                SetCommunityStatus("Event deleted.", ImGuiColors.HealerGreen);
            }

            _eventDeleteOperations.Remove(entry.Key);
        }
    }

    private void ConsumeDigestOperation()
    {
        if (!_digestOperation.IsCompleted)
            return;

        if (_digestOperation.Faulted)
        {
            SetCommunityStatus(_digestOperation.Error ?? "Unable to update the Discord digest.", ImGuiColors.DalamudRed);
            _digestLoaded = true;
        }
        else
        {
            var digest = _digestOperation.Result!;
            _digestEnabled = digest.Enabled;
            _digestWebhookDraft = digest.WebhookUrl ?? string.Empty;
            _digestMinuteUtc = digest.MinuteOfDayUtc;
            _digestLoaded = true;
            if (_digestSavePending)
                SetCommunityStatus("Discord digest settings saved.", ImGuiColors.HealerGreen);
        }

        _digestSavePending = false;
        _digestOperation.Reset();
    }

    private void ConsumeListingOperations()
    {
        ConsumeListingResult(_listingLoadOperation, "Unable to load directory listing.");
        ConsumeListingResult(_listingSaveOperation, "Unable to save directory listing.", "Directory listing saved.");
    }

    private bool ConsumeCommunityResult(AsyncOp<GroupCommunityDto> operation, string failureMessage, string? successMessage = null)
    {
        if (!operation.IsCompleted)
        {
            return false;
        }

        var succeeded = !operation.Faulted;
        if (succeeded)
        {
            ApplyCommunity(operation.Result);
            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                SetCommunityStatus(successMessage, ImGuiColors.HealerGreen);
            }
        }
        else
        {
            SetCommunityStatus(operation.Error ?? failureMessage, ImGuiColors.DalamudRed);
        }

        operation.Reset();
        return succeeded;
    }

    private void ConsumeListingResult(AsyncOp<GroupDirectoryListingDto> operation, string failureMessage, string? successMessage = null)
    {
        if (!operation.IsCompleted)
        {
            return;
        }

        var succeeded = !operation.Faulted;
        if (succeeded)
        {
            ApplyListing(operation.Result);
            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                SetListingStatus(successMessage, ImGuiColors.HealerGreen);
            }
        }
        else
        {
            SetListingStatus(operation.Error ?? failureMessage, ImGuiColors.DalamudRed);
        }

        operation.Reset();
    }

    private void ApplyCommunity(GroupCommunityDto? community)
    {
        if (community == null)
        {
            return;
        }

        _community = community;
        _motdDraft = community.Motd ?? string.Empty;
        _mediator.Publish(new GroupCommunityUpdatedMessage(community));
    }

    private void ApplyListing(GroupDirectoryListingDto? listing)
    {
        if (listing == null)
        {
            return;
        }

        _listing = listing;
        _listingDescriptionDraft = listing.Description ?? string.Empty;
        _listingTagsDraft = string.Join(", ", listing.Tags);
        _mainWorldDraft = listing.MainWorldId is uint worldId and > 0 and <= ushort.MaxValue ? (ushort)worldId : (ushort)0;
        _mainRegionDraft = !string.IsNullOrEmpty(listing.MainRegion)
            ? listing.MainRegion
            : (_mainWorldDraft != 0 ? _dalamudUtilService.GetWorldRegion(_mainWorldDraft) ?? string.Empty : string.Empty);
    }

    private AsyncOp<GroupCommunityDto> GetEventDeleteOperation(Guid eventId)
    {
        if (!_eventDeleteOperations.TryGetValue(eventId, out var operation))
        {
            operation = new AsyncOp<GroupCommunityDto>();
            _eventDeleteOperations[eventId] = operation;
        }

        return operation;
    }

    private void SetCommunityStatus(string status, Vector4 colour)
    {
        _communityStatus = status;
        _communityStatusColour = colour;
    }

    private void SetListingStatus(string status, Vector4 colour)
    {
        _listingStatus = status;
        _listingStatusColour = colour;
    }

    private static List<string> ParseTags(string tags)
    {
        return tags
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private static List<string> ParseEventValues(string values)
        => values.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();

    private static string FormatJoinPolicy(GroupDirectoryJoinPolicy policy)
    {
        return policy switch
        {
            GroupDirectoryJoinPolicy.Open => "Open",
            GroupDirectoryJoinPolicy.Request => "Request",
            GroupDirectoryJoinPolicy.InviteOnly => "Invite only",
            _ => policy.ToString(),
        };
    }

    private static string CreateDefaultEventStart()
        => DateTime.Now.AddDays(1).ToString("g", CultureInfo.CurrentCulture);

    private static void DrawStatus(string status, Vector4 colour)
    {
        if (!string.IsNullOrWhiteSpace(status))
        {
            ElezenImgui.ColouredWrappedText(status, colour);
        }
    }

    private static void DrawOperationStatus(AsyncOp operation, string runningText)
    {
        if (operation.IsRunning)
        {
            ImGui.SameLine();
            ElezenImgui.ColouredText(runningText, ImGuiColors.DalamudYellow);
        }
        else if (operation.Faulted)
        {
            ImGui.SameLine();
            ElezenImgui.ColouredText(operation.Error ?? "Failed", ImGuiColors.DalamudRed);
        }
    }
}
