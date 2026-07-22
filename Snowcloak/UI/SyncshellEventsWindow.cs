using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Roleplay;
using Snowcloak.Core.IO;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.UI.Components;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Snowcloak.UI;

public sealed class SyncshellEventsWindow : WindowMediatorSubscriberBase
{
    private static readonly Action<ILogger, Exception?> LogEventBannerUploadFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, nameof(UploadEventBannerAsync)), "Could not upload event banner");
    private static readonly Action<ILogger, Exception?> LogEventBannerRenderFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(2, nameof(DrawBannerImage)), "Failed to load event banner image");
    // An event counts as "active" for one hour after its start time. This mirrors the
    // calendar indicator shown on the syncshell row in the main UI.
    private static readonly TimeSpan EventActiveWindow = TimeSpan.FromHours(1);

    private readonly ApiController _apiController;
    private readonly FileDialogManager _fileDialogManager;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly TextureService _textureService;
    private readonly ImageTransferService _imageTransferService;
    private readonly Dictionary<string, IDalamudTextureWrap> _bannerTextures = new(StringComparer.Ordinal);
    private readonly AsyncOp<GroupCommunityDto> _communityLoadOperation = new();
    private readonly AsyncOp<GroupCommunityDto> _eventOperation = new();
    private readonly AsyncOp<GroupEventDigestConfigDto> _digestOperation = new();
    private GroupCommunityDto? _community;
    private string _status = string.Empty;
    private Guid _editEventId;
    private string _eventTitle = string.Empty;
    private string _eventDescription = string.Empty;
    private string _eventStart = DateTime.Now.AddHours(1).ToString("g", CultureInfo.CurrentCulture);
    private int _eventDurationMinutes = 120;
    private string _eventPlot = string.Empty;
    private string _eventTags = string.Empty;
    private string _eventWarnings = string.Empty;
    private string _eventHosts = string.Empty;
    private string _eventWorldId = string.Empty;
    private int _eventCapacity;
    private string _eventBannerHash = string.Empty;
    private bool _eventBannerUploading;
    private bool _eventPublic;
    private ProfileContentRating _eventRating;
    private bool _eventRecurring;
    private RpEventRecurrenceFrequency _eventFrequency = RpEventRecurrenceFrequency.Weekly;
    private int _eventInterval = 1;
    private int _eventDaysMask;
    private string _eventUntil = string.Empty;
    private int _eventOccurrenceCount;
    private bool _digestEnabled;
    private string _digestWebhook = string.Empty;
    private int _digestMinuteUtc = 720;

    public SyncshellEventsWindow(ILogger<SyncshellEventsWindow> logger, SnowMediator mediator,
        ApiController apiController, PairManager pairManager, DalamudUtilService dalamudUtilService,
        TextureService textureService, ImageTransferService imageTransferService, FileDialogManager fileDialogManager,
        GroupFullInfoDto groupFullInfo, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, BuildWindowTitle(groupFullInfo), performanceCollectorService)
    {
        ArgumentNullException.ThrowIfNull(groupFullInfo);
        GroupFullInfo = groupFullInfo;
        _apiController = apiController;
        _fileDialogManager = fileDialogManager;
        _pairManager = pairManager;
        _dalamudUtilService = dalamudUtilService;
        _textureService = textureService;
        _imageTransferService = imageTransferService;
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var texture in _bannerTextures.Values)
                texture.Dispose();
            _bannerTextures.Clear();
        }
        base.Dispose(disposing);
    }

    protected override void DrawInternal()
    {
        ConsumeCommunityLoad();
        ConsumeEventOperation();
        ConsumeDigestOperation();

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

        var owner = string.Equals(GroupFullInfo.Owner.UID, _apiController.UID, StringComparison.Ordinal);
        var canAuthor = owner && _apiController.SupportsRpFeatures;
        if (canAuthor)
        {
            DrawEventAuthoring();
            ModernSection.SoftSeparator();
        }
        else if (owner)
        {
            ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                "Roleplay event authoring is unavailable on this server.");
            ModernSection.SoftSeparator();
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
            var end = DateTime.SpecifyKind(shellEvent.EndsAtUtc.GetValueOrDefault(start + EventActiveWindow), DateTimeKind.Utc);
            if (start <= nowUtc && nowUtc < end)
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
                DrawEventRow(shellEvent, nowUtc, active: true, canAuthor);
            }

            ImGuiHelpers.ScaledDummy(4f);
        }

        if (upcoming.Count > 0)
        {
            ImGui.TextUnformatted("Upcoming");
            ImGui.Separator();
            foreach (var shellEvent in upcoming.OrderBy(e => e.StartsAtUtc))
            {
                DrawEventRow(shellEvent, nowUtc, active: false, canAuthor);
            }
        }
    }

    private void DrawEventRow(GroupEventDto shellEvent, DateTime nowUtc, bool active, bool canAuthor)
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

        if (!string.IsNullOrWhiteSpace(shellEvent.BannerImageHash))
            DrawBannerImage(shellEvent.BannerImageHash);

        if (canAuthor)
        {
            if (ImGui.SmallButton("Edit")) LoadEventDraft(shellEvent);
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete"))
                _ = _eventOperation.Run(() => _apiController.GroupDeleteEvent(new GroupEventDeleteDto(GroupFullInfo.Group, shellEvent.Id)));
        }

        ImGui.Separator();
    }

    private void StartCommunityLoad()
    {
        _status = string.Empty;
        _communityLoadOperation.Reset();
        _ = _communityLoadOperation.Run(() => _apiController.GroupGetCommunity(new GroupDto(GroupFullInfo.Group)));
        if (_apiController.SupportsRpFeatures
            && string.Equals(GroupFullInfo.Owner.UID, _apiController.UID, StringComparison.Ordinal))
            _ = _digestOperation.Run(() => _apiController.RpEventDigestGet(new GroupDto(GroupFullInfo.Group)));
    }

    private void DrawEventAuthoring()
    {
        ModernSection.Header(FontAwesomeIcon.CalendarPlus, _editEventId == Guid.Empty ? "Create event" : "Edit event");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##event-title", "Event title", ref _eventTitle, 120);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextMultiline("##event-description", ref _eventDescription, 2000, new Vector2(-1f, 64f * ImGuiHelpers.GlobalScale));
        ImGui.SetNextItemWidth(190f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##event-start", "Local start date and time", ref _eventStart, 80);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(115f * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Duration (min)", ref _eventDurationMinutes, 15, 60);
        _eventDurationMinutes = Math.Clamp(_eventDurationMinutes, 15, 10080);
        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##event-plot", "Plot / location", ref _eventPlot, 160);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##event-tags", "Theme tags", ref _eventTags, 200);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##event-warnings", "Content warnings", ref _eventWarnings, 300);
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##event-hosts", "Hosts, comma-separated", ref _eventHosts, 300);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##event-world", "World ID", ref _eventWorldId, 12);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("Capacity", ref _eventCapacity);
        _eventCapacity = Math.Max(0, _eventCapacity);
        ImGui.TextUnformatted("Banner (720x300 PNG)");
        using (ImRaii.Disabled(_eventBannerUploading))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileUpload, "Upload banner"))
            {
                _fileDialogManager.OpenFileDialog("Select event banner", ".png", (success, file) =>
                {
                    if (success)
                        _ = UploadEventBannerAsync(file);
                });
            }
        }
        ElezenImgui.AttachTooltip("Upload a 720x300 PNG banner.");
        ImGui.SameLine();
        using (ImRaii.Disabled(_eventBannerUploading || string.IsNullOrWhiteSpace(_eventBannerHash)))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Clear banner"))
                _eventBannerHash = string.Empty;
        }
        if (_eventBannerUploading)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Uploading...");
        }
        if (!string.IsNullOrWhiteSpace(_eventBannerHash))
            DrawBannerImage(_eventBannerHash.Trim());
        ImGui.Checkbox("Public directory listing", ref _eventPublic);
        ElezenImgui.AttachTooltip("Public events allow eligible players to join this syncshell without a password.");
        ImGui.SameLine();
        DrawEnumCombo("##event-rating", ref _eventRating);
        ImGui.SameLine();
        ImGui.Checkbox("Recurring", ref _eventRecurring);
        if (_eventRecurring)
        {
            ImGui.SameLine();
            DrawEnumCombo("##event-frequency", ref _eventFrequency);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f * ImGuiHelpers.GlobalScale);
            ImGui.InputInt("Interval", ref _eventInterval);
            _eventInterval = Math.Clamp(_eventInterval, 1, 52);
            if (_eventFrequency == RpEventRecurrenceFrequency.Weekly)
            {
                string[] days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
                foreach (var (day, index) in days.Select((day, index) => (day, index)))
                {
                    var selected = (_eventDaysMask & (1 << index)) != 0;
                    if (ImGui.Checkbox(day + "##event-day", ref selected))
                    {
                        if (selected) _eventDaysMask |= 1 << index; else _eventDaysMask &= ~(1 << index);
                    }
                    ImGui.SameLine();
                }
                ImGui.NewLine();
            }
            ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("##event-until", "Optional local end date", ref _eventUntil, 80);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110f * ImGuiHelpers.GlobalScale);
            ImGui.InputInt("Occurrences", ref _eventOccurrenceCount);
            _eventOccurrenceCount = Math.Max(0, _eventOccurrenceCount);
        }
        using (ImRaii.Disabled(_eventOperation.IsRunning || _eventBannerUploading || string.IsNullOrWhiteSpace(_eventTitle)))
            if (ImGui.Button(_editEventId == Guid.Empty ? "Create event" : "Save event")) SaveEvent();
        if (_editEventId != Guid.Empty)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(_eventBannerUploading))
                if (ImGui.Button("Cancel edit")) ResetEventDraft();
        }

        ImGuiHelpers.ScaledDummy(8f);
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Webhook digest");
        ImGui.Checkbox("Enabled##digest", ref _digestEnabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##digest-webhook", "Webhook URL", ref _digestWebhook, 500);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f * ImGuiHelpers.GlobalScale);
        ImGui.InputInt("UTC minute", ref _digestMinuteUtc, 30, 60);
        _digestMinuteUtc = Math.Clamp(_digestMinuteUtc, 0, 1439);
        ImGui.SameLine();
        using (ImRaii.Disabled(_digestOperation.IsRunning))
            if (ImGui.Button("Save digest"))
                _ = _digestOperation.Run(() => _apiController.RpEventDigestSet(new GroupEventDigestConfigDto
                {
                    Group = GroupFullInfo.Group,
                    Enabled = _digestEnabled,
                    WebhookUrl = string.IsNullOrWhiteSpace(_digestWebhook) ? null : _digestWebhook.Trim(),
                    MinuteOfDayUtc = _digestMinuteUtc,
                }));
    }

    private void SaveEvent()
    {
        if (!DateTime.TryParse(_eventStart, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var startLocal))
        {
            _status = "Enter a valid local start date and time.";
            return;
        }
        var startUtc = startLocal.ToUniversalTime();
        uint? worldId = uint.TryParse(_eventWorldId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedWorld) && parsedWorld > 0 ? parsedWorld : null;
        DateTime? untilUtc = DateTime.TryParse(_eventUntil, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsedUntil)
            ? parsedUntil.ToUniversalTime()
            : null;
        var shellEvent = new GroupEventDto(_editEventId == Guid.Empty ? Guid.NewGuid() : _editEventId, _eventTitle.Trim(), startUtc)
        {
            Description = string.IsNullOrWhiteSpace(_eventDescription) ? null : _eventDescription.Trim(),
            EndsAtUtc = startUtc.AddMinutes(_eventDurationMinutes),
            Plot = string.IsNullOrWhiteSpace(_eventPlot) ? null : _eventPlot.Trim(),
            ThemeTags = SplitTerms(_eventTags),
            ContentWarnings = SplitTerms(_eventWarnings),
            Hosts = SplitTerms(_eventHosts),
            WorldId = worldId,
            Capacity = _eventCapacity > 0 ? _eventCapacity : null,
            BannerImageHash = string.IsNullOrWhiteSpace(_eventBannerHash) ? null : _eventBannerHash.Trim(),
            IsPublic = _eventPublic,
            ContentRating = _eventRating,
            Recurrence = _eventRecurring ? new GroupEventRecurrenceDto
            {
                Frequency = _eventFrequency,
                Interval = _eventInterval,
                DaysOfWeekMask = _eventFrequency == RpEventRecurrenceFrequency.Weekly ? _eventDaysMask : 0,
                UntilUtc = untilUtc,
                OccurrenceCount = _eventOccurrenceCount > 0 ? _eventOccurrenceCount : null,
            } : null,
        };
        _ = _eventOperation.Run(() => _apiController.GroupUpsertEvent(new GroupEventUpsertDto(GroupFullInfo.Group, shellEvent)));
    }

    private async Task UploadEventBannerAsync(string filePath)
    {
        _eventBannerUploading = true;
        _status = "Uploading event banner...";
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            var dimensions = PngHeaderReader.TryExtractDimensions(stream);
            if (dimensions.Width != 720 || dimensions.Height != 300)
            {
                _status = "Event banners must be exactly 720x300 pixels.";
                return;
            }

            var reply = await _imageTransferService.UploadImageAsync(bytes, ImageKind.VenueBanner, CancellationToken.None).ConfigureAwait(false);
            if (reply == null || string.IsNullOrEmpty(reply.Hash))
            {
                _status = "Could not upload that event banner.";
                return;
            }

            _eventBannerHash = reply.Hash;
            _status = $"Banner uploaded. Image storage: {FormatImageQuota(reply.AccountUsageBytes, reply.AccountQuotaBytes)}. Save the event to publish it.";
        }
        catch (ImageUploadException ex)
        {
            _status = ex.Message;
        }
        catch (IOException ex)
        {
            SetEventBannerUploadFailure(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            SetEventBannerUploadFailure(ex);
        }
        catch (HttpRequestException ex)
        {
            SetEventBannerUploadFailure(ex);
        }
        catch (InvalidOperationException ex)
        {
            SetEventBannerUploadFailure(ex);
        }
        catch (JsonException ex)
        {
            SetEventBannerUploadFailure(ex);
        }
        finally
        {
            _eventBannerUploading = false;
        }
    }

    private void SetEventBannerUploadFailure(Exception exception)
    {
        LogEventBannerUploadFailure(_logger, exception);
        _status = "Could not upload that event banner.";
    }

    private void LoadEventDraft(GroupEventDto shellEvent)
    {
        _editEventId = shellEvent.Id;
        _eventTitle = shellEvent.Title;
        _eventDescription = shellEvent.Description ?? string.Empty;
        _eventStart = shellEvent.StartsAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        _eventDurationMinutes = Math.Max(15, (int)(shellEvent.EndsAtUtc.GetValueOrDefault(shellEvent.StartsAtUtc.AddHours(2)) - shellEvent.StartsAtUtc).TotalMinutes);
        _eventPlot = shellEvent.Plot ?? string.Empty;
        _eventTags = string.Join(", ", shellEvent.ThemeTags);
        _eventWarnings = string.Join(", ", shellEvent.ContentWarnings);
        _eventHosts = string.Join(", ", shellEvent.Hosts);
        _eventWorldId = shellEvent.WorldId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _eventCapacity = shellEvent.Capacity ?? 0;
        _eventBannerHash = shellEvent.BannerImageHash ?? string.Empty;
        _eventPublic = shellEvent.IsPublic;
        _eventRating = shellEvent.ContentRating;
        _eventRecurring = shellEvent.Recurrence != null;
        _eventFrequency = shellEvent.Recurrence?.Frequency ?? RpEventRecurrenceFrequency.Weekly;
        _eventInterval = shellEvent.Recurrence?.Interval ?? 1;
        _eventDaysMask = shellEvent.Recurrence?.DaysOfWeekMask ?? 0;
        _eventUntil = shellEvent.Recurrence?.UntilUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;
        _eventOccurrenceCount = shellEvent.Recurrence?.OccurrenceCount ?? 0;
    }

    private void ResetEventDraft()
    {
        _editEventId = Guid.Empty;
        _eventTitle = string.Empty;
        _eventDescription = string.Empty;
        _eventStart = DateTime.Now.AddHours(1).ToString("g", CultureInfo.CurrentCulture);
        _eventDurationMinutes = 120;
        _eventPlot = string.Empty;
        _eventTags = string.Empty;
        _eventWarnings = string.Empty;
        _eventHosts = string.Empty;
        _eventWorldId = string.Empty;
        _eventCapacity = 0;
        _eventBannerHash = string.Empty;
        _eventPublic = false;
        _eventRating = ProfileContentRating.General;
        _eventRecurring = false;
        _eventDaysMask = 0;
        _eventUntil = string.Empty;
        _eventOccurrenceCount = 0;
    }

    private void ConsumeEventOperation()
    {
        if (!_eventOperation.IsCompleted) return;
        if (_eventOperation.Faulted) _status = _eventOperation.Error ?? "Unable to save the event.";
        else
        {
            _community = _eventOperation.Result;
            Mediator.Publish(new GroupCommunityUpdatedMessage(_community!));
            _status = "Event schedule updated.";
            ResetEventDraft();
        }
        _eventOperation.Reset();
    }

    private void ConsumeDigestOperation()
    {
        if (!_digestOperation.IsCompleted) return;
        if (_digestOperation.Faulted) _status = _digestOperation.Error ?? "Unable to update the webhook digest.";
        else
        {
            var digest = _digestOperation.Result!;
            _digestEnabled = digest.Enabled;
            _digestWebhook = digest.WebhookUrl ?? string.Empty;
            _digestMinuteUtc = digest.MinuteOfDayUtc;
        }
        _digestOperation.Reset();
    }

    private static void DrawEnumCombo<T>(string id, ref T value) where T : struct, Enum
    {
        ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
        if (!ImGui.BeginCombo(id, value.ToString())) return;
        foreach (var item in Enum.GetValues<T>())
            if (ImGui.Selectable(item.ToString(), EqualityComparer<T>.Default.Equals(item, value))) value = item;
        ImGui.EndCombo();
    }

    private void DrawBannerImage(string bannerHash)
    {
        if (_bannerTextures.TryGetValue(bannerHash, out var texture))
        {
            var availableWidth = Math.Max(0f, ImGui.GetContentRegionAvail().X);
            var sourceSize = new Vector2(texture.Width, texture.Height);
            var scale = sourceSize.X > 0f ? Math.Min(1f, availableWidth / sourceSize.X) : 1f;
            ImGui.Image(texture.Handle, sourceSize * scale);
            return;
        }

        if (_imageTransferService.TryGetImage(bannerHash, out var bytes) && bytes.Length > 0)
        {
            try
            {
                _bannerTextures[bannerHash] = _textureService.LoadImage(bytes);
                DrawBannerImage(bannerHash);
            }
            catch (Exception ex) when (IsExpectedTextureFailure(ex))
            {
                LogEventBannerRenderFailure(_logger, ex);
                ImGui.TextColored(ImGuiColors.DalamudRed, "Failed to load event banner.");
            }
            return;
        }

        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Loading event banner...");
    }

    private static List<string> SplitTerms(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsExpectedTextureFailure(Exception exception)
        => exception is AggregateException or InvalidDataException or IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or ObjectDisposedException or InvalidOperationException;

    private static string FormatImageQuota(long usageBytes, long quotaBytes)
    {
        var usage = (usageBytes / 1024d / 1024d).ToString("0.#", CultureInfo.InvariantCulture);
        if (quotaBytes <= 0)
            return $"{usage} MiB used";
        var quota = (quotaBytes / 1024d / 1024d).ToString("0.#", CultureInfo.InvariantCulture);
        return $"{usage} of {quota} MiB";
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
