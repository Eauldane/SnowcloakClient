using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ElezenTools.UI;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto.Account;
using Snowcloak.API.Dto.User;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Handlers;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.UI.Handlers;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;

namespace Snowcloak.UI;

public partial class CompactUi
{
    private void DrawPendingPairRequestsSection()
    {
        var availability = _pairRequestService.AvailabilityStore.State;
        if (!availability.PairingEnabled)
            return;

        PendingPairRequestSection.Draw(availability.PendingRequests, _availabilityDispatcher, _tagHandler,
            "CompactUI", indent: true, collapsibleWhenNoTag: false);
    }

    private void DrawAddPair()
    {
        var framePadding = ImGui.GetStyle().FramePadding;
        var tallPadding = new Vector2(framePadding.X, framePadding.Y + 4f * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, tallPadding);
        var buttonSize = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Plus);
        var clearButtonSize = ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Times);
        var entryIcon = FontAwesomeIcon.Link;
        var entryIconWidth = ElezenImgui.GetIconData(entryIcon).X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
        {
            ElezenImgui.ShowIcon(entryIcon);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ElezenImgui.GetWindowContentRegionWidth()
            - ImGui.GetWindowContentRegionMin().X
            - entryIconWidth
            - clearButtonSize.X
            - buttonSize.X
            - spacing * 3);
        ImGui.InputTextWithHint("##otheruid", "Other players UID/Alias", ref _pairToAdd, 20);
        ImGui.SameLine();
        if (ElezenImgui.IconButton(FontAwesomeIcon.Times))
        {
            _pairToAdd = string.Empty;
        }
        ElezenImgui.AttachTooltip("Clear");
        ImGui.SameLine();
        var canAdd = !_pairManager.DirectPairs.Any(p => string.Equals(p.UserData.UID, _pairToAdd, StringComparison.Ordinal) || string.Equals(p.UserData.Alias, _pairToAdd, StringComparison.Ordinal));
        using (ImRaii.Disabled(!canAdd))
        {
            if (ElezenImgui.IconButton(FontAwesomeIcon.Plus))
            {
                _ = _apiController.UserAddPair(new(new(_pairToAdd)));
                _pairToAdd = string.Empty;
            }
            ElezenImgui.AttachTooltip(string.Format(CultureInfo.InvariantCulture, "Send pair request to {0}", _pairToAdd.IsNullOrEmpty() ? "another player" : _pairToAdd));
        }
        ImGui.PopStyleVar();

        ImGuiHelpers.ScaledDummy(10);
    }

    private void DrawFilter()
    {
        ImGuiHelpers.ScaledDummy(6);
        var scale = ImGuiHelpers.GlobalScale;

        var users = GetFilteredUsers();
        var userCount = users.Count;

        var framePadding = ImGui.GetStyle().FramePadding;
        var filterPadding = new Vector2(framePadding.X, framePadding.Y + 4f * scale);
        var filterHeight = ImGui.GetFontSize() + filterPadding.Y * 2f;
        var button = _buttonState ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;
        var playButtonSize = ElezenImgui.GetIconButtonSize(button) with { Y = filterHeight };
        var spacing = userCount > 0
            ? playButtonSize.X + ImGui.GetStyle().ItemSpacing.X
            : 0;
        var filterPanelMin = ImGui.GetCursorScreenPos();
        var filterPanelMax = filterPanelMin + new Vector2(_windowContentWidth, filterHeight);
        ImGui.GetWindowDrawList().AddRectFilled(filterPanelMin, filterPanelMax, Colour.Vector4ToColour(new Vector4(0.040f, 0.080f, 0.115f, 0.82f)), 3f * scale);
        ImGui.GetWindowDrawList().AddLine(filterPanelMin, filterPanelMin with { X = filterPanelMax.X }, Colour.Vector4ToColour(new Vector4(SnowcloakColours.CompactBorderSubtle.X, SnowcloakColours.CompactBorderSubtle.Y, SnowcloakColours.CompactBorderSubtle.Z, 0.45f)), 1f * scale);
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, filterPadding))
        {
            ImGui.SetNextItemWidth(_windowContentWidth - spacing);
            ImGui.InputTextWithHint("##filter", "Filter for UID/notes", ref _characterOrCommentFilter, 255);
        }
        
        if (userCount == 0) return;

        var pausedUsers = users.Where(u => u.UserPair!.OwnPermissions.IsPaused() && u.UserPair.OtherPermissions.IsPaired()).ToList();
        var resumedUsers = users.Where(u => !u.UserPair!.OwnPermissions.IsPaused() && u.UserPair.OtherPermissions.IsPaired()).ToList();

        if (pausedUsers.Count == 0 && resumedUsers.Count == 0) return;
        ImGui.SameLine();

        switch (_buttonState)
        {
            case true when pausedUsers.Count == 0:
                _buttonState = false;
                break;

            case false when resumedUsers.Count == 0:
                _buttonState = true;
                break;

            case true:
                users = pausedUsers;
                break;

            case false:
                users = resumedUsers;
                break;
        }

        if (_timeout.ElapsedMilliseconds > 5000)
            _timeout.Reset();

        button = _buttonState ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause;

        using (ImRaii.Disabled(_timeout.IsRunning))
        {
            if (ElezenImgui.IconButton(button, filterHeight) && ElezenImgui.CtrlPressed())
            {
                foreach (var entry in users)
                {
                    var perm = entry.UserPair!.OwnPermissions;
                    perm.SetPaused(!perm.IsPaused());
                    _ = _apiController.UserSetPairPermissions(new UserPermissionsDto(entry.UserData, perm));
                }

                _timeout.Start();
                _buttonState = !_buttonState;
            }
            if (!_timeout.IsRunning)
                ElezenImgui.AttachTooltip(string.Format(CultureInfo.InvariantCulture, "Hold Control to {0} pairing with {1} out of {2} displayed users.", button == FontAwesomeIcon.Play ? "resume" : "pause", users.Count, userCount));
            else
                ElezenImgui.AttachTooltip(string.Format(CultureInfo.InvariantCulture, "Next execution is available at {0} seconds", (5000 - _timeout.ElapsedMilliseconds) / 1000));
        }
    }

    private void DrawPairList()
    {
        using (ImRaii.PushId("addpair")) DrawAddPair();
        using (ImRaii.PushId("pairs")) DrawPairs();
        _transferPartHeight = ImGui.GetCursorPosY();
        using (ImRaii.PushId("filter")) DrawFilter();
    }

    private void DrawPairs()
    {
        var bottomReserve = Math.Max(_transferPartHeight, ImGui.GetFrameHeightWithSpacing() + 16f * ImGuiHelpers.GlobalScale);
        var ySize = (ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y) - bottomReserve - ImGui.GetCursorPosY();
        ySize = Math.Max(1f, ySize);
        var users = GetFilteredUsers()
            .Where(u => u.UserPair != null)
            .OrderBy(u => u.GetPairSortKey(), StringComparer.Ordinal)
            .ToList();

        var onlineUsers = new List<DrawUserPair>();
        var pausedUsers = new List<DrawUserPair>();
        var visibleUsers = new List<DrawUserPair>();
        var offlineUsers = new List<DrawUserPair>();

        foreach (var user in users)
        {
            var pair = user.UserPair!;
            var isPaired = pair.OtherPermissions.IsPaired();
            var otherPaused = pair.OtherPermissions.IsPaused();
            var ownPaused = pair.OwnPermissions.IsPaused();

            if (isPaired && user.IsOnline && !user.IsVisible && !otherPaused && !ownPaused)
                onlineUsers.Add(GetPairRow("Online", user));
            if (isPaired && ownPaused)
                pausedUsers.Add(GetPairRow("Paused", user));
            if (user.IsVisible)
                visibleUsers.Add(GetPairRow("Visible", user));
            if (!isPaired || (!ownPaused && (!user.IsOnline || otherPaused)))
                offlineUsers.Add(GetPairRow("Offline", user));
        }

        PrunePairRowCache(users.Count);

        using var listBg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.018f, 0.052f, 0.078f, 0.16f));
        using var listPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(6f, 4f) * ImGuiHelpers.GlobalScale);
        ImGui.BeginChild("list", new Vector2(_windowContentWidth, ySize), border: false);

        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, 7f * ImGuiHelpers.GlobalScale)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2f * ImGuiHelpers.GlobalScale)))
        {
            _pairGroupsUi.Draw(visibleUsers, onlineUsers, pausedUsers, offlineUsers);
        }

        DrawPendingPairRequestsSection();
        
        ImGui.EndChild();
    }

    private readonly Dictionary<string, DrawUserPair> _pairRowCache = new(StringComparer.Ordinal);

    private DrawUserPair GetPairRow(string prefix, Pair pair)
    {
        var id = prefix + pair.UserData.UID;
        if (!_pairRowCache.TryGetValue(id, out var row) || !ReferenceEquals(row.Pair, pair))
        {
            row = new DrawUserPair(id, pair, _uidDisplayHandler, _apiController, Mediator, _selectGroupForPairUi,
                _charaDataManager, _configService);
            _pairRowCache[id] = row;
        }

        return row;
    }

    private void PrunePairRowCache(int activeUserCount)
    {
        if (_pairRowCache.Count > activeUserCount * 4 + 64)
            _pairRowCache.Clear();
    }

    private List<Pair> GetFilteredUsers()
    {
        return _pairManager.DirectPairs
            .Where(p => p.UserPair != null)
            .Where(p =>
            {
                if (_characterOrCommentFilter.IsNullOrEmpty()) return true;
                return p.UserData.AliasOrUID.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ||
                       (p.GetNote()?.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                       (p.PlayerName?.Contains(_characterOrCommentFilter, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .ToList();
    }

    private static bool DrawCompactIconButton(FontAwesomeIcon icon, Vector2 size, string id)
    {
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##compact-icon-{id}", size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var max = min + size;
        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();

        var bg = hovered
            ? new Vector4(0.120f, 0.190f, 0.285f, 0.92f)
            : new Vector4(0.060f, 0.105f, 0.155f, 0.82f);
        var border = hovered ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactBorderSubtle;
        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(bg), 5f * scale);
        drawList.AddRect(min, max, Colour.Vector4ToColour(border), 5f * scale, ImDrawFlags.None, 1f * scale);

        var iconText = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText);
        drawList.AddText(min + (size - iconSize) * 0.5f, Colour.Vector4ToColour(Vector4.One), iconText);
        ImGui.PopFont();

        return clicked;
    }
}
