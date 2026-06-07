using Dalamud.Bindings.ImGui;
using Dalamud.Game.Player;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ElezenTools.Data;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Services;
using Snowcloak.Services.Events;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using System;
using System.Linq;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class PairingAvailabilityWindow : WindowMediatorSubscriberBase
{
    private const double HookCycleSeconds = 5.0;
    private const double HookFadeSeconds = 0.75;
    private readonly PairRequestService _pairRequestService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly SnowProfileManager _profileManager;
    private readonly SnowcloakConfigService _configService;
    private bool _locked;
    private List<AvailabilityEntry> _lockedEntries = new();
    private readonly TitleBarButton _lockButton;
    private string _lockTooltip;
    
    public PairingAvailabilityWindow(ILogger<PairingAvailabilityWindow> logger, SnowMediator mediator,
        PairRequestService pairRequestService, DalamudUtilService dalamudUtilService, SnowProfileManager profileManager,
        SnowcloakConfigService configService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "SnowcloakPairingAvailability", performanceCollectorService)
    {
        _pairRequestService = pairRequestService;
        _dalamudUtilService = dalamudUtilService;
        _profileManager = profileManager;
        _configService = configService;

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
        var rawAvailable = _locked
            ? _lockedEntries
            : BuildAvailabilityEntries();
        var viewerTags = GetViewerProfileTags();
        var available = ApplyProfileFilters(rawAvailable, viewerTags);

        DrawProfileFilterControls(rawAvailable.Count, available.Count, filteredCount);

        if (rawAvailable.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No nearby Frostbrand users are currently open to pairing.");
            if (filteredCount > 0)
                ImGui.TextColored(ImGuiColors.DalamudGrey,
                    string.Format("({0} nearby players filtered by auto-reject settings)", filteredCount));
            return;
        }

        if (available.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No nearby Frostbrand users match the current profile filters.");
            return;
        }

        if (_configService.Current.FrostbrandUseProfileCards)
            DrawPlayerCards(available, viewerTags);
        else
            DrawPlayerTable(available);
    }

    private void DrawPlayerTable(IReadOnlyList<AvailabilityEntry> available)
    {
        using var table = ImRaii.Table("pairing-availability-table", 10, ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders | ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Resizable);
        if (table) {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 0.18f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 0.12f);
            ImGui.TableSetupColumn("Tagline", ImGuiTableColumnFlags.WidthStretch, 0.24f);
            ImGui.TableSetupColumn("Pronouns", ImGuiTableColumnFlags.WidthStretch, 0.1f);
            ImGui.TableSetupColumn("Game Gender", ImGuiTableColumnFlags.WidthFixed, 85f);
            ImGui.TableSetupColumn("Tribe", ImGuiTableColumnFlags.WidthFixed, 105f);
            ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthFixed, 65f);
            ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Approach", ImGuiTableColumnFlags.WidthStretch, 0.12f);
            ImGui.TableSetupColumn("Homeworld", ImGuiTableColumnFlags.WidthStretch, 0.12f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            ImGuiClip.ClippedDraw(available, this.DrawPlayer, ImGui.GetTextLineHeightWithSpacing());
        }
    }

    private void DrawProfileFilterControls(int totalCount, int visibleCount, int autoRejectedCount)
    {
        var config = _configService.Current;
        var changed = false;

        var useCards = config.FrostbrandUseProfileCards;
        if (ImGui.Checkbox("Profile cards", ref useCards))
        {
            config.FrostbrandUseProfileCards = useCards;
            changed = true;
        }

        ImGui.SameLine();
        var onlyWithProfiles = config.FrostbrandOnlyWithProfiles;
        if (ImGui.Checkbox("Only with profiles", ref onlyWithProfiles))
        {
            config.FrostbrandOnlyWithProfiles = onlyWithProfiles;
            changed = true;
        }

        ImGui.SameLine();
        var search = config.FrostbrandProfileSearch ?? string.Empty;
        var remainingWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var searchWidth = MathF.Min(320f * ImGuiHelpers.GlobalScale, MathF.Max(120f * ImGuiHelpers.GlobalScale, remainingWidth * 0.58f));
        ImGui.SetNextItemWidth(searchWidth);
        if (ImGui.InputTextWithHint("##frostbrand-profile-search", "Search name, profile, race, tribe, class, world...", ref search, 160))
        {
            config.FrostbrandProfileSearch = search;
            changed = true;
        }

        ImGui.SameLine();
        var requiredTag = config.FrostbrandRequiredTag ?? string.Empty;
        ImGui.SetNextItemWidth(MathF.Min(240f * ImGuiHelpers.GlobalScale, MathF.Max(100f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X)));
        if (ImGui.InputTextWithHint("##frostbrand-profile-tag-filter", "Tag filter...", ref requiredTag, 80))
        {
            config.FrostbrandRequiredTag = requiredTag;
            changed = true;
        }

        if (changed)
            _configService.Save();

        ImGui.TextColored(ImGuiColors.DalamudGrey, $"{visibleCount}/{totalCount} Frostbrand users shown");
        if (autoRejectedCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"({autoRejectedCount} filtered by auto-reject settings)");
        }
        ImGui.Separator();
    }

    private List<AvailabilityEntry> ApplyProfileFilters(IReadOnlyList<AvailabilityEntry> entries, IReadOnlyList<UserProfileTagDto> viewerTags)
    {
        var config = _configService.Current;
        var searchQuery = ProfileTagUtilities.NormalizeForLookup(config.FrostbrandProfileSearch);
        var tagQuery = ProfileTagUtilities.NormalizeForLookup(config.FrostbrandRequiredTag);

        return entries
            .Where(entry => !config.FrostbrandOnlyWithProfiles || HasMeaningfulProfile(entry, viewerTags))
            .Where(entry => string.IsNullOrEmpty(searchQuery) || MatchesSearch(entry, searchQuery, viewerTags))
            .Where(entry => string.IsNullOrEmpty(tagQuery) || MatchesRequiredTag(entry, tagQuery, viewerTags))
            .ToList();
    }

    private bool MatchesSearch(AvailabilityEntry entry, string query, IReadOnlyList<UserProfileTagDto> viewerTags)
    {
        return EnumerateSearchFields(entry, viewerTags)
            .Select(ProfileTagUtilities.NormalizeForLookup)
            .Any(field => field.Contains(query, StringComparison.Ordinal));
    }

    private static bool MatchesRequiredTag(AvailabilityEntry entry, string query, IReadOnlyList<UserProfileTagDto> viewerTags)
    {
        return GetVisibleTagsForViewer(entry.Profile?.Tags, viewerTags).Any(tag =>
            ProfileTagUtilities.NormalizeForLookup(tag.Value).Contains(query, StringComparison.Ordinal)
            || ProfileTagUtilities.NormalizeForLookup(ProfileTagChipRenderer.GetTypeLabel(tag.Type)).Contains(query, StringComparison.Ordinal)
            || ProfileTagUtilities.NormalizeForLookup($"{ProfileTagChipRenderer.GetTypeLabel(tag.Type)}:{tag.Value}").Contains(query, StringComparison.Ordinal));
    }

    private IEnumerable<string> EnumerateSearchFields(AvailabilityEntry entry, IReadOnlyList<UserProfileTagDto> viewerTags)
    {
        yield return entry.DisplayName;
        yield return GetProfileDisplayName(entry);
        yield return GetStatus(entry);
        yield return GetHomeWorldName(entry);
        yield return GetClassName(entry);
        yield return GetGameGender(entry);
        yield return GetRaceName(entry);
        yield return GetTribeName(entry);
        if (entry.Level > 0)
            yield return entry.Level.ToString();

        var profile = entry.Profile;
        if (profile == null)
            yield break;

        yield return profile.CharacterName;
        yield return profile.Title;
        yield return profile.Pronouns;
        yield return profile.Tagline;
        yield return profile.RpStatus;
        yield return profile.Approachability;
        foreach (var hook in profile.Hooks)
        {
            yield return hook.Title;
            yield return hook.Description;
        }
        foreach (var tag in GetVisibleTagsForViewer(profile.Tags, viewerTags))
        {
            yield return tag.Value;
            yield return ProfileTagChipRenderer.GetTypeLabel(tag.Type);
        }
    }

    private static bool HasMeaningfulProfile(AvailabilityEntry entry, IReadOnlyList<UserProfileTagDto> viewerTags)
    {
        var profile = entry.Profile;
        return profile != null
               && (!string.IsNullOrWhiteSpace(profile.CharacterName)
                   || !string.IsNullOrWhiteSpace(profile.Title)
                   || !string.IsNullOrWhiteSpace(profile.Pronouns)
                   || !string.IsNullOrWhiteSpace(profile.Tagline)
                   || !string.IsNullOrWhiteSpace(profile.RpStatus)
                   || !string.IsNullOrWhiteSpace(profile.Approachability)
                   || profile.Hooks.Count > 0
                   || GetVisibleTagsForViewer(profile.Tags, viewerTags).Count > 0);
    }

    private IReadOnlyList<UserProfileTagDto> GetViewerProfileTags()
    {
        var ownProfile = _profileManager.GetOwnProfile(ProfileVisibility.Private);
        return ownProfile.Revision > 0 ? ownProfile.Tags : [];
    }

    private static IReadOnlyList<UserProfileTagDto> GetVisibleTagsForViewer(IReadOnlyList<UserProfileTagDto>? profileTags,
        IReadOnlyList<UserProfileTagDto> viewerTags)
    {
        return ProfileTagUtilities.GetVisibleTagsForViewer(profileTags, viewerTags);
    }

    private void DrawPlayerCards(IReadOnlyList<AvailabilityEntry> available, IReadOnlyList<UserProfileTagDto> viewerTags)
    {
        using var child = ImRaii.Child("pairing-availability-cards", Vector2.Zero, true);
        if (!child)
            return;

        foreach (var entry in available)
        {
            DrawPlayerCard(entry, viewerTags);
            ImGui.Spacing();
        }
    }

    private void DrawPlayerCard(AvailabilityEntry entry, IReadOnlyList<UserProfileTagDto> viewerTags)
    {
        using var id = ImRaii.PushId($"frostbrand-card-{entry.Ident}");
        using var card = ImRaii.Child("card", new Vector2(0f, 260f * ImGuiHelpers.GlobalScale), true);
        if (!card)
            return;

        ImGui.TextColored(ImGuiColors.DalamudWhite, GetProfileDisplayName(entry));
        DrawContextMenu(entry);
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.HealerGreen, GetStatus(entry));

        ImGui.TextColored(ImGuiColors.DalamudGrey,
            $"{GetGameGender(entry)}  |  {GetTribeName(entry)}  |  {GetClassName(entry)} {GetLevelText(entry)}  |  {GetHomeWorldName(entry)}");

        if (!string.IsNullOrWhiteSpace(entry.Profile?.Tagline))
            ImGui.TextWrapped(entry.Profile.Tagline);
        else if (!HasMeaningfulProfile(entry, viewerTags))
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No RP profile summary published yet.");

        DrawCardLabelValue("Pronouns:", entry.Profile?.Pronouns);
        DrawCardLabelValue("Approach:", entry.Profile?.Approachability);
        DrawRotatingHook(entry);
        DrawCardTags(GetVisibleTagsForViewer(entry.Profile?.Tags, viewerTags));
        DrawCardActions(entry);
    }

    private void DrawCardActions(AvailabilityEntry entry)
    {
        if (ImGui.Button("View Profile"))
            _ = _pairRequestService.RequestProfileAsync(entry.Ident);
        ImGui.SameLine();
        if (ImGui.Button("Pair Request"))
            _ = _pairRequestService.SendPairRequestAsync(entry.Ident);
        ImGui.SameLine();
        if (ImGui.Button("Examine"))
            _ = HandleExamineAsync(entry);
        ImGui.SameLine();
        if (ImGui.Button("Plate"))
            _ = HandleAdventurerPlateAsync(entry);
    }

    private static void DrawCardLabelValue(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        ImGui.TextColored(ImGuiColors.DalamudGrey, label);
        ImGui.SameLine();
        ImGui.TextWrapped(value);
    }

    private static void DrawRotatingHook(AvailabilityEntry entry)
    {
        var hooks = entry.Profile?.Hooks
            .Where(hook => !string.IsNullOrWhiteSpace(hook.Title) || !string.IsNullOrWhiteSpace(hook.Description))
            .ToList();
        if (hooks == null || hooks.Count == 0)
            return;

        var (hook, alpha, index) = SelectDisplayedHook(entry.Ident, hooks);
        ImGui.Spacing();
        ImGui.TextColored(WithAlpha(ImGuiColors.DalamudGrey, alpha), hooks.Count == 1 ? "RP Hook:" : $"RP Hook {index + 1}/{hooks.Count}:");
        using (ImRaii.PushColor(ImGuiCol.Text, WithAlpha(ImGuiColors.HealerGreen, alpha)))
        {
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(hook.Title) ? "Hook" : hook.Title);
        }

        if (!string.IsNullOrWhiteSpace(hook.Description))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, WithAlpha(ImGuiColors.DalamudWhite, alpha)))
            {
                ImGui.TextWrapped(hook.Description);
            }
        }
    }

    private static (CharacterProfileHookDto Hook, float Alpha, int Index) SelectDisplayedHook(string ident, IReadOnlyList<CharacterProfileHookDto> hooks)
    {
        if (hooks.Count == 1)
            return (hooks[0], 1f, 0);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var offset = GetStablePhaseOffset(ident);
        var cyclePosition = (now + offset) % (hooks.Count * HookCycleSeconds);
        var index = (int)(cyclePosition / HookCycleSeconds);
        var localPosition = cyclePosition - index * HookCycleSeconds;
        var fadeIn = localPosition / HookFadeSeconds;
        var fadeOut = (HookCycleSeconds - localPosition) / HookFadeSeconds;
        var alpha = (float)Math.Clamp(Math.Min(fadeIn, fadeOut), 0.08, 1.0);
        return (hooks[index], alpha, index);
    }

    private static double GetStablePhaseOffset(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in value)
                hash = (hash ^ character) * 16777619u;
            return hash % 1000 / 1000.0 * HookCycleSeconds;
        }
    }

    private static Vector4 WithAlpha(Vector4 color, float alpha)
        => new(color.X, color.Y, color.Z, color.W * alpha);

    private static void DrawCardTags(IReadOnlyList<UserProfileTagDto> tags)
    {
        if (tags.Count == 0)
            return;

        var visibleTags = tags.Take(6).Select(tag => $"{ProfileTagChipRenderer.GetTypeLabel(tag.Type)}: {tag.Value}");
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Tags:");
        ImGui.SameLine();
        ImGui.TextWrapped(string.Join("   ", visibleTags));
    }

    private void DrawPlayer(AvailabilityEntry entry)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetProfileDisplayName(entry));
        DrawContextMenu(entry);
        ImGui.TableNextColumn();
        ImGui.TextColored(ImGuiColors.HealerGreen, GetStatus(entry));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.Profile?.Tagline) ? "-" : entry.Profile.Tagline);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.Profile?.Pronouns) ? "-" : entry.Profile.Pronouns);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetGameGender(entry));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetTribeName(entry));
        ImGui.TableNextColumn();
        ImGui.TextColored(GetClassColor(entry), GetClassName(entry));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetLevelText(entry));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.Profile?.Approachability) ? "-" : entry.Profile.Approachability);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(GetHomeWorldName(entry));
    }

    private static string GetProfileDisplayName(AvailabilityEntry entry)
        => string.IsNullOrWhiteSpace(entry.Profile?.CharacterName) ? entry.DisplayName : entry.Profile.CharacterName;

    private static string GetStatus(AvailabilityEntry entry)
        => string.IsNullOrWhiteSpace(entry.Profile?.RpStatus) ? "Open to pairing" : entry.Profile.RpStatus;

    private static string GetGameGender(AvailabilityEntry entry)
    {
        return entry.Sex switch
        {
            Sex.Male => "Male",
            Sex.Female => "Female",
            _ => "-",
        };
    }

    private string GetTribeName(AvailabilityEntry entry)
    {
        return entry.TribeId is > 0 and <= byte.MaxValue
               && _dalamudUtilService.TribeNames.Value.TryGetValue((byte)entry.TribeId, out var tribe)
            ? tribe
            : "-";
    }

    private static string GetRaceName(AvailabilityEntry entry)
    {
        return entry.TribeId switch
        {
            1 or 2 => "Hyur",
            3 or 4 => "Elezen",
            5 or 6 => "Lalafell",
            7 or 8 => "Miqo'te",
            9 or 10 => "Roegadyn",
            11 or 12 => "Au Ra",
            13 or 14 => "Hrothgar",
            15 or 16 => "Viera",
            _ => "-",
        };
    }

    private static string GetClassName(AvailabilityEntry entry)
    {
        return entry.ClassJobId switch
        {
            1 => "GLA",
            2 => "PGL",
            3 => "MRD",
            4 => "LNC",
            5 => "ARC",
            6 => "CNJ",
            7 => "THM",
            8 => "CRP",
            9 => "BSM",
            10 => "ARM",
            11 => "GSM",
            12 => "LTW",
            13 => "WVR",
            14 => "ALC",
            15 => "CUL",
            16 => "MIN",
            17 => "BTN",
            18 => "FSH",
            19 => "PLD",
            20 => "MNK",
            21 => "WAR",
            22 => "DRG",
            23 => "BRD",
            24 => "WHM",
            25 => "BLM",
            26 => "ACN",
            27 => "SMN",
            28 => "SCH",
            29 => "ROG",
            30 => "NIN",
            31 => "MCH",
            32 => "DRK",
            33 => "AST",
            34 => "SAM",
            35 => "RDM",
            36 => "BLU",
            37 => "GNB",
            38 => "DNC",
            39 => "RPR",
            40 => "SGE",
            41 => "VPR",
            42 => "PCT",
            _ => "-",
        };
    }

    private static Vector4 GetClassColor(AvailabilityEntry entry)
    {
        return ElezenData.Jobs.GetById(entry.ClassJobId)?.ClassColour ?? ImGuiColors.DalamudGrey;
    }

    private static string GetLevelText(AvailabilityEntry entry)
        => entry.Level > 0 ? entry.Level.ToString() : "-";

    private string GetHomeWorldName(AvailabilityEntry entry)
    {
        return entry.HomeWorldId.HasValue
               && _dalamudUtilService.WorldData.Value.TryGetValue(entry.HomeWorldId.Value, out var world)
            ? world
            : "-";
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
                string.IsNullOrWhiteSpace(tuple.pc.Name) ? "Unnamed character" : tuple.pc.Name,
                tuple.pc.HomeWorldId != 0 ? (ushort?)tuple.pc.HomeWorldId : null,
                tuple.pc.ClassJobId,
                tuple.pc.Level,
                tuple.pc.Sex,
                tuple.pc.TribeId,
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

    private readonly record struct AvailabilityEntry(string Ident, string DisplayName, ushort? HomeWorldId, uint ClassJobId, short Level, Sex Sex, uint TribeId, CharacterProfileSummaryDto? Profile);
    
}
