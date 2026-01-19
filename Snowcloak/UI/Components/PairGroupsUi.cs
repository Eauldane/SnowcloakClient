using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data.Extensions;
using Snowcloak.Configuration;
using Snowcloak.UI.Handlers;
using Snowcloak.WebAPI;

namespace Snowcloak.UI.Components;

public class PairGroupsUi
{
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _snowcloakConfig;
    private readonly SelectPairForGroupUi _selectGroupForPairUi;
    private readonly TagHandler _tagHandler;
    private readonly UidDisplayHandler _uidDisplayHandler;
    private readonly UiSharedService _uiSharedService;

    public PairGroupsUi(SnowcloakConfigService snowcloakConfig, TagHandler tagHandler, UidDisplayHandler uidDisplayHandler, ApiController apiController,
        SelectPairForGroupUi selectGroupForPairUi, UiSharedService uiSharedService)
    {
        _snowcloakConfig = snowcloakConfig;
        _tagHandler = tagHandler;
        _uidDisplayHandler = uidDisplayHandler;
        _apiController = apiController;
        _selectGroupForPairUi = selectGroupForPairUi;
        _uiSharedService = uiSharedService;
    }

    public void Draw<T>(List<T> visibleUsers, List<T> onlineUsers, List<T> pausedUsers, List<T> offlineUsers) where T : DrawPairBase
    {
        // Only render those tags that actually have pairs in them, otherwise
        // we can end up with a bunch of useless pair groups
        var tagsWithPairsInThem = _tagHandler.GetAllTagsSorted();
        var allUsers = onlineUsers.Concat(offlineUsers).Concat(pausedUsers).ToList();
        if (typeof(T) == typeof(DrawUserPair))
        {
            DrawUserPairs(tagsWithPairsInThem, allUsers.Cast<DrawUserPair>().ToList(), visibleUsers.Cast<DrawUserPair>(), onlineUsers.Cast<DrawUserPair>(), pausedUsers.Cast<DrawUserPair>(), offlineUsers.Cast<DrawUserPair>());
        }
    }

    private void DrawButtons(string tag, List<DrawUserPair> availablePairsInThisTag)
    {
        var allArePaused = availablePairsInThisTag.All(pair => pair.UserPair!.OwnPermissions.IsPaused());
        var pauseButton = allArePaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var flyoutMenuX = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Bars).X;
        var pauseButtonX = _uiSharedService.GetIconButtonSize(pauseButton).X;
        var windowX = ImGui.GetWindowContentRegionMin().X;
        var windowWidth = UiSharedService.GetWindowContentRegionWidth();
        var spacingX = ImGui.GetStyle().ItemSpacing.X;

        var buttonPauseOffset = windowX + windowWidth - flyoutMenuX - spacingX - pauseButtonX;
        ImGui.SameLine(buttonPauseOffset);
        if (_uiSharedService.IconButton(pauseButton))
        {
            // If all of the currently visible pairs (after applying filters to the pairs)
            // are paused we display a resume button to resume all currently visible (after filters)
            // pairs. Otherwise, we just pause all the remaining pairs.
            if (allArePaused)
            {
                // If all are paused => resume all
                ResumeAllPairs(availablePairsInThisTag);
            }
            else
            {
                // otherwise pause all remaining
                PauseRemainingPairs(availablePairsInThisTag);
            }
        }
        if (allArePaused)
        {
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "Resume pairing with all pairs in {0}", tag));
        }
        else
        {
            UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "Pause pairing with all pairs in {0}", tag));
        }

        var buttonDeleteOffset = windowX + windowWidth - flyoutMenuX;
        ImGui.SameLine(buttonDeleteOffset);
        if (_uiSharedService.IconButton(FontAwesomeIcon.Bars))
        {
            ImGui.OpenPopup("Group Flyout Menu");
        }

        if (ImGui.BeginPopup("Group Flyout Menu"))
        {
            using (ImRaii.PushId($"buttons-{tag}")) DrawGroupMenu(tag);
            ImGui.EndPopup();
        }
    }

    private void DrawCategory(string tag, IEnumerable<DrawPairBase> onlineUsers, IEnumerable<DrawPairBase> pausedUsers, IEnumerable<DrawPairBase> allUsers, IEnumerable<DrawPairBase>? visibleUsers = null)
    {
        IEnumerable<DrawPairBase> usersInThisTag;
        HashSet<string>? otherUidsTaggedWithTag = null;
        bool isSpecialTag = false;
        int visibleInThisTag = 0;

        if (tag is TagHandler.CustomPausedTag)
        {
            usersInThisTag = pausedUsers;
            isSpecialTag = true;
        }
        else if (tag is TagHandler.CustomOfflineTag or TagHandler.CustomOnlineTag or TagHandler.CustomVisibleTag or TagHandler.CustomUnpairedTag)
        {
            usersInThisTag = onlineUsers;
            isSpecialTag = true;
        }
        else
        {
            otherUidsTaggedWithTag = _tagHandler.GetOtherUidsForTag(tag);
            usersInThisTag = onlineUsers
                .Where(pair => otherUidsTaggedWithTag.Contains(pair.UID))
                .ToList();
            visibleInThisTag = visibleUsers?.Count(p => otherUidsTaggedWithTag.Contains(p.UID)) ?? 0;
        }

        if (isSpecialTag && !usersInThisTag.Any()) return;

        DrawName(tag, isSpecialTag, visibleInThisTag, usersInThisTag.Count(), pausedUsers.Count(), otherUidsTaggedWithTag?.Count);
        if (!isSpecialTag)
        {
            using (ImRaii.PushId($"group-{tag}-buttons")) DrawButtons(tag, allUsers.Cast<DrawUserPair>().Where(p => otherUidsTaggedWithTag!.Contains(p.UID)).ToList());
        }
        else
        {
            if (!_tagHandler.IsTagOpen(tag))
            {
                var size = ImGui.CalcTextSize("").Y + ImGui.GetStyle().FramePadding.Y * 2f;
                ImGui.SameLine();
                ImGui.Dummy(new(size, size));
            }
        }

        if (!_tagHandler.IsTagOpen(tag)) return;

        ImGui.Indent(20);
        DrawPairs(tag, usersInThisTag);
        ImGui.Unindent(20);
    }

    private void DrawGroupMenu(string tag)
    {
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Users, string.Format(CultureInfo.CurrentCulture, "Add people to {0}", tag)))
        {
            _selectGroupForPairUi.Open(tag);
        }
        UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "Add more users to Group {0}", tag));
        
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, string.Format(CultureInfo.CurrentCulture, "Delete {0}", tag)) && UiSharedService.CtrlPressed())
        {
            _tagHandler.RemoveTag(tag);
        }
        UiSharedService.AttachToolTip(string.Format(CultureInfo.CurrentCulture, "Delete Group {0} (Will not delete the pairs){1}Hold CTRL to delete", tag, Environment.NewLine));
    }

    private void DrawName(string tag, bool isSpecialTag, int visible, int online, int paused, int? total)
    {
        string displayedName = tag switch
        {
            TagHandler.CustomUnpairedTag => "Unpaired",
            TagHandler.CustomOfflineTag => "Offline",
            TagHandler.CustomOnlineTag => _snowcloakConfig.Current.ShowOfflineUsersSeparately ? "Online" : "Contacts",
            TagHandler.CustomPausedTag => "Paused",
            TagHandler.CustomVisibleTag => "Visible",
            _ => tag
        };

        string resultFolderName = !isSpecialTag
            ? string.Format(CultureInfo.CurrentCulture, "{0} ({1}/{2}/{3}/{4} Pairs)", displayedName, visible, online, paused, total)
            : string.Format(CultureInfo.CurrentCulture, "{0} ({1} Pairs)", displayedName, online);
        //  FontAwesomeIcon.CaretSquareDown : FontAwesomeIcon.CaretSquareRight
        var icon = _tagHandler.IsTagOpen(tag) ? FontAwesomeIcon.CaretSquareDown : FontAwesomeIcon.CaretSquareRight;
        _uiSharedService.IconText(icon);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            ToggleTagOpen(tag);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(resultFolderName);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            ToggleTagOpen(tag);
        }

        if (!isSpecialTag && ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "Group {0}", tag));
            ImGui.Separator();
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "{0} Pairs visible", visible));
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "{0} Pairs online", online));
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "{0} Pairs paused", paused));
            ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture, "{0} Pairs total", total));
            ImGui.EndTooltip();
        }
    }

    private void DrawPairs(string tag, IEnumerable<DrawPairBase> availablePairsInThisCategory)
    {
        // These are all the OtherUIDs that are tagged with this tag
        _uidDisplayHandler.RenderPairList(availablePairsInThisCategory);
        ImGui.Separator();
    }

    private void DrawUserPairs(List<string> tagsWithPairsInThem, List<DrawUserPair> allUsers, IEnumerable<DrawUserPair> visibleUsers, IEnumerable<DrawUserPair> onlineUsers, IEnumerable<DrawUserPair> pausedUsers, IEnumerable<DrawUserPair> offlineUsers)
    {
        if (_snowcloakConfig.Current.ShowVisibleUsersSeparately)
        {
            using (ImRaii.PushId("$group-VisibleCustomTag")) DrawCategory(TagHandler.CustomVisibleTag, visibleUsers, Enumerable.Empty<DrawUserPair>(), allUsers);
        }

        foreach (var tag in tagsWithPairsInThem)
        {
            if (_snowcloakConfig.Current.ShowOfflineUsersSeparately)
            {
                using (ImRaii.PushId($"group-{tag}")) DrawCategory(tag, onlineUsers, pausedUsers, allUsers, visibleUsers);
            }
            else
            {
                using (ImRaii.PushId($"group-{tag}")) DrawCategory(tag, allUsers, Enumerable.Empty<DrawUserPair>(), allUsers, visibleUsers);
            }
        }

        if (_snowcloakConfig.Current.ShowOfflineUsersSeparately)
        {
            using (ImRaii.PushId($"group-OnlineCustomTag")) DrawCategory(TagHandler.CustomOnlineTag,
                onlineUsers.Where(u => !_tagHandler.HasAnyTag(u.UID)).ToList(), Enumerable.Empty<DrawUserPair>(), allUsers);
            if (pausedUsers.Any()) using (ImRaii.PushId("group-PausedCustomTag")) DrawCategory(TagHandler.CustomPausedTag,
                Enumerable.Empty<DrawUserPair>(), pausedUsers, allUsers);
            using (ImRaii.PushId($"group-OfflineCustomTag")) DrawCategory(TagHandler.CustomOfflineTag,
                offlineUsers.Where(u => u.UserPair!.OtherPermissions.IsPaired()).ToList(), Enumerable.Empty<DrawUserPair>(), allUsers);
        }
        else
        {
            using (ImRaii.PushId($"group-OnlineCustomTag")) DrawCategory(TagHandler.CustomOnlineTag,
                onlineUsers.Concat(offlineUsers.Where(u => u.UserPair!.OtherPermissions.IsPaired())).Where(u => !_tagHandler.HasAnyTag(u.UID)).ToList(), Enumerable.Empty<DrawUserPair>(), allUsers);
        }

        using (ImRaii.PushId($"group-UnpairedCustomTag")) DrawCategory(TagHandler.CustomUnpairedTag,
            offlineUsers.Where(u => !u.UserPair!.OtherPermissions.IsPaired()).ToList(), Enumerable.Empty<DrawUserPair>(), allUsers);
    }


    private void PauseRemainingPairs(List<DrawUserPair> availablePairs)
    {
        foreach (var pairToPause in availablePairs.Where(pair => !pair.UserPair!.OwnPermissions.IsPaused()))
        {
            var perm = pairToPause.UserPair!.OwnPermissions;
            perm.SetPaused(paused: true);
            _ = _apiController.UserSetPairPermissions(new(new(pairToPause.UID), perm));
        }
    }

    private void ResumeAllPairs(List<DrawUserPair> availablePairs)
    {
        foreach (var pairToPause in availablePairs)
        {
            var perm = pairToPause.UserPair!.OwnPermissions;
            perm.SetPaused(paused: false);
            _ = _apiController.UserSetPairPermissions(new(new(pairToPause.UID), perm));
        }
    }

    private void ToggleTagOpen(string tag)
    {
        bool open = !_tagHandler.IsTagOpen(tag);
        _tagHandler.SetTagOpen(tag, open);
    }
}