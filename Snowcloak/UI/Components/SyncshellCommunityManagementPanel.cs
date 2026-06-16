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
using Snowcloak.Services;
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
    private readonly AsyncOp<GroupCommunityDto> _communityLoadOperation = new();
    private readonly AsyncOp<GroupCommunityDto> _motdSaveOperation = new();
    private readonly AsyncOp<GroupCommunityDto> _eventCreateOperation = new();
    private readonly AsyncOp<GroupDirectoryListingDto> _listingLoadOperation = new();
    private readonly AsyncOp<GroupDirectoryListingDto> _listingSaveOperation = new();
    private readonly Dictionary<Guid, AsyncOp<GroupCommunityDto>> _eventDeleteOperations = [];
    private string _activeGid = string.Empty;
    private GroupCommunityDto? _community;
    private GroupDirectoryListingDto? _listing;
    private string _motdDraft = string.Empty;
    private string _eventTitleDraft = string.Empty;
    private string _eventDescriptionDraft = string.Empty;
    private string _eventStartDraft = CreateDefaultEventStart();
    private string _listingDescriptionDraft = string.Empty;
    private string _listingTagsDraft = string.Empty;
    private ushort _mainWorldDraft;
    private string _mainRegionDraft = string.Empty;
    private string _communityStatus = string.Empty;
    private Vector4 _communityStatusColour = ImGuiColors.DalamudYellow;
    private string _listingStatus = string.Empty;
    private Vector4 _listingStatusColour = ImGuiColors.DalamudYellow;

    public SyncshellCommunityManagementPanel(ApiController apiController, DalamudUtilService dalamudUtilService)
    {
        _apiController = apiController;
        _dalamudUtilService = dalamudUtilService;
    }

    public void DrawCommunity(GroupFullInfoDto group)
    {
        EnsureGroup(group);
        ConsumeCommunityOperations();
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
        ImGui.Separator();
        DrawEventList(group, community);
        ImGui.Separator();
        DrawEventEditor(group);
    }

    public void DrawDirectory(GroupFullInfoDto group)
    {
        EnsureGroup(group);
        ConsumeListingOperations();
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
        _eventTitleDraft = string.Empty;
        _eventDescriptionDraft = string.Empty;
        _eventStartDraft = CreateDefaultEventStart();
        _listingDescriptionDraft = string.Empty;
        _listingTagsDraft = string.Empty;
        _mainWorldDraft = 0;
        _mainRegionDraft = string.Empty;
        _communityStatus = string.Empty;
        _listingStatus = string.Empty;
        _communityLoadOperation.Reset();
        _motdSaveOperation.Reset();
        _eventCreateOperation.Reset();
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
        ImGui.TextUnformatted("Events");
        if (community.Events.Count == 0)
        {
            ElezenImgui.ColouredWrappedText("No events scheduled.", ImGuiColors.DalamudGrey);
            return;
        }

        foreach (var shellEvent in community.Events.OrderBy(e => e.StartsAtUtc))
        {
            DrawEventRow(group, shellEvent);
        }
    }

    private void DrawEventRow(GroupFullInfoDto group, GroupEventDto shellEvent)
    {
        using var id = ImRaii.PushId("admin-event-" + shellEvent.Id.ToString("N"));
        ImGui.AlignTextToFramePadding();
        ElezenImgui.ShowIcon(FontAwesomeIcon.Calendar, SnowcloakColours.CompactTextMuted);
        ImGui.SameLine();
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "{0:g}  {1}", shellEvent.StartsAtUtc.ToLocalTime(), shellEvent.Title));

        var deleteOperation = GetEventDeleteOperation(shellEvent.Id);
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

        if (!string.IsNullOrWhiteSpace(shellEvent.Description))
        {
            ElezenImgui.WrappedText(shellEvent.Description);
        }
    }

    private void DrawEventEditor(GroupFullInfoDto group)
    {
        ImGui.TextUnformatted("Add event");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##eventtitle", "Event title", ref _eventTitleDraft, 100);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##eventstart", "Event date and time", ref _eventStartDraft, 64);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##eventdescription", "Event description", ref _eventDescriptionDraft, 512);

        var hasValidStart = DateTime.TryParse(_eventStartDraft, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsedStart);
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_eventTitleDraft) || !hasValidStart || _eventCreateOperation.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add event") && hasValidStart)
            {
                var dto = new GroupEventDto(Guid.NewGuid(), _eventTitleDraft.Trim(), parsedStart.ToUniversalTime())
                {
                    Description = string.IsNullOrWhiteSpace(_eventDescriptionDraft) ? null : _eventDescriptionDraft.Trim(),
                    ReminderEnabled = true
                };
                _communityStatus = string.Empty;
                _ = _eventCreateOperation.Run(() => _apiController.GroupUpsertEvent(new GroupEventUpsertDto(group.Group, dto)));
            }
        }
        DrawOperationStatus(_eventCreateOperation, "Adding...");
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

    private void ConsumeCommunityOperations()
    {
        ConsumeCommunityResult(_communityLoadOperation, "Unable to load community details.");
        ConsumeCommunityResult(_motdSaveOperation, "Unable to save MOTD.", "MOTD saved.");

        if (ConsumeCommunityResult(_eventCreateOperation, "Unable to add event.", "Event added."))
        {
            _eventTitleDraft = string.Empty;
            _eventDescriptionDraft = string.Empty;
            _eventStartDraft = CreateDefaultEventStart();
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
