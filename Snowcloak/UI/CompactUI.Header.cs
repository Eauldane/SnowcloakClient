using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using ElezenTools.UI;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.API.Dto;
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
    private void DrawAnnouncementBanners()
    {
        if (_apiController.ServerState is not ServerState.Connected)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var announcements = _apiController.SystemInfoDto.Announcements
            .Where(a => !_dismissedAnnouncementIds.Contains(a.Id))
            .Where(a => a.StartsAtUtc == default || a.StartsAtUtc <= now)
            .Where(a => !a.EndsAtUtc.HasValue || a.EndsAtUtc.Value >= now)
            .OrderByDescending(a => a.IsMaintenance)
            .ThenByDescending(a => a.Severity)
            .Take(2)
            .ToList();

        if (announcements.Count == 0)
        {
            return;
        }

        foreach (var announcement in announcements)
        {
            DrawAnnouncementBanner(announcement);
            ImGuiHelpers.ScaledDummy(6);
        }
    }

    private void DrawAnnouncementBanner(ServerAnnouncementDto announcement)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var colour = GetAnnouncementColour(announcement);
        var min = ImGui.GetCursorScreenPos();
        var width = ElezenImgui.GetWindowContentRegionWidth();
        var textWidth = Math.Max(80f * scale, width - 74f * scale);
        var text = announcement.IsMaintenance ? "Maintenance: " + announcement.Text : announcement.Text;
        var textSize = ImGui.CalcTextSize(text);
        var height = Math.Max(34f * scale, textSize.Y + 16f * scale);
        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(new Vector4(colour.X * 0.16f, colour.Y * 0.16f, colour.Z * 0.16f, 0.86f)), 3f * scale);
        drawList.AddRect(min, max, Colour.Vector4ToColour(new Vector4(colour.X, colour.Y, colour.Z, 0.64f)), 3f * scale, ImDrawFlags.None, 1f * scale);

        ImGui.Dummy(new Vector2(width, height));

        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorScreenPos(min + new Vector2(12f * scale, (height - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(colour, GetAnnouncementIcon(announcement).ToIconString());
        ImGui.PopFont();

        ImGui.SetCursorScreenPos(min + new Vector2(38f * scale, 8f * scale));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + textWidth);
        ImGui.TextColored(Vector4.One, text);
        ImGui.PopTextWrapPos();
        if (!string.IsNullOrWhiteSpace(announcement.Url) && ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            Util.OpenLink(announcement.Url);
        }
        if (!string.IsNullOrWhiteSpace(announcement.Url))
        {
            ElezenImgui.AttachTooltip(announcement.Url);
        }

        ImGui.SetCursorScreenPos(min + new Vector2(width - 32f * scale, (height - 24f * scale) * 0.5f));
        if (DrawCompactIconButton(FontAwesomeIcon.Times, new Vector2(24f, 24f) * scale, "dismiss-announcement-" + announcement.Id.ToString("N")))
        {
            _dismissedAnnouncementIds.Add(announcement.Id);
        }
        ElezenImgui.AttachTooltip("Dismiss");
        ImGui.SetCursorPos(cursor);
    }

    private static FontAwesomeIcon GetAnnouncementIcon(ServerAnnouncementDto announcement)
    {
        if (announcement.IsMaintenance)
        {
            return FontAwesomeIcon.Tools;
        }

        return announcement.Severity switch
        {
            MessageSeverity.Error => FontAwesomeIcon.ExclamationCircle,
            MessageSeverity.Warning => FontAwesomeIcon.ExclamationTriangle,
            _ => FontAwesomeIcon.InfoCircle
        };
    }

    private static Vector4 GetAnnouncementColour(ServerAnnouncementDto announcement)
    {
        if (announcement.IsMaintenance)
        {
            return ImGuiColors.DalamudYellow;
        }

        return announcement.Severity switch
        {
            MessageSeverity.Error => ImGuiColors.DalamudRed,
            MessageSeverity.Warning => ImGuiColors.DalamudYellow,
            _ => SnowcloakColours.OnlineBlue
        };
    }

    private void DrawUIDHeader()
    {
        var uidText = GetUidText();
        var headerStart = ImGui.GetCursorPos();
        var uidColour = GetUidColor();
        var uidGlowColour = GetUidGlowColor();

        ImGuiHelpers.ScaledDummy(7);
        using (_fontService.UidFont.Push())
        {
            var uidTextSize = ImGui.CalcTextSize(uidText);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (uidTextSize.X / 2));
            ElezenImgui.ColouredText(uidText, uidColour, uidGlowColour);
        }

        if (_apiController.ServerState is ServerState.Connected)
        {
            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(_apiController.DisplayName);
            }
            ElezenImgui.AttachTooltip("Click to copy");
            
                        
            if (!string.Equals(_apiController.DisplayName, _apiController.UID, StringComparison.Ordinal))
            {
                var origTextSize = ImGui.CalcTextSize(_apiController.UID);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (origTextSize.X / 2));
                ElezenImgui.ColouredText(_apiController.UID, uidColour, uidGlowColour);
                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText(_apiController.UID);
                }
                ElezenImgui.AttachTooltip("Click to copy");
            }
            
            var headerEnd = ImGui.GetCursorPos();
            var buttonSize = new Vector2(34f, 30f) * ImGuiHelpers.GlobalScale;
            var buttonHeight = buttonSize.Y;
            var buttonWidth = buttonSize.X;
            var buttonX = ImGui.GetWindowContentRegionMax().X - buttonWidth;
            var buttonY = headerStart.Y + ((headerEnd.Y - headerStart.Y) - buttonHeight) / 2f;
            ImGui.SetCursorPos(new Vector2(buttonX, buttonY));
            using (ImRaii.PushId("vanity-id-edit"))
            {
                if (DrawCompactIconButton(FontAwesomeIcon.Pen, buttonSize, "vanity-id-edit"))
                {
                    _vanityIdInput = _apiController.VanityId ?? string.Empty;
                    _patreonLoginFeedback = null;
                    _patreonLoginFeedbackLevel = PatreonLoginFeedbackLevel.None;
                    if (_apiController.HexAllowed)
                    {
                        _useVanityColour = !string.IsNullOrWhiteSpace(_apiController.DisplayColour)
                                           || !string.IsNullOrWhiteSpace(_apiController.DisplayGlowColour);
                        _vanityColour = Colour.HexToVector3OrDefault(_apiController.DisplayColour, Vector3.One);
                        _useVanityGlowColour = !string.IsNullOrWhiteSpace(_apiController.DisplayGlowColour);
                        _vanityGlowColour = Colour.HexToVector3OrDefault(_apiController.DisplayGlowColour, Vector3.Zero);
                    }
                    _showVanityIdModal = true;
                    RefreshPatreonStatus();
                }
            }
            ElezenImgui.AttachTooltip("Edit vanity ID");
            ImGui.SetCursorPos(headerEnd);
        }

        if (_apiController.ServerState is not ServerState.Connected)
        {
            ElezenImgui.ColouredWrappedText(GetServerError(), GetUidColor());
            if (_apiController.ServerState is ServerState.NoSecretKey)
            {
                DrawAddCharacter();
            }
        }
        ImGuiHelpers.ScaledDummy(10);
    }

    private string GetServerError()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => "Attempting to connect to the server.",
            ServerState.Reconnecting => string.IsNullOrWhiteSpace(_apiController.AuthFailureMessage)
                ? "Connection to server interrupted, attempting to reconnect to the server."
                : _apiController.AuthFailureMessage,
            ServerState.Disconnected => "You are currently disconnected from the sync server.",
            ServerState.Disconnecting => "Disconnecting from the server",
            ServerState.Unauthorized => string.Format(CultureInfo.InvariantCulture, "Server Response: {0}", _apiController.AuthFailureMessage),
            ServerState.Offline => "Your selected sync server is currently offline.",
            ServerState.VersionMisMatch =>
               "Your plugin or the server you are connecting to is out of date. Please update your plugin now. If you already did so, contact the server provider to update their server to the latest version.",
            ServerState.RateLimited => "You are rate limited for (re)connecting too often. Disconnect, wait 10 minutes and try again.",
            ServerState.Connected => string.Empty,
            ServerState.NoSecretKey => "You have no secret key set for this current character. Use the button below or open the settings and set a secret key for the current character. You can reuse the same secret key for multiple characters.",
            ServerState.MultiChara => "Your Character Configuration has multiple characters configured with same name and world. You will not be able to connect until you fix this issue. Remove the duplicates from the configuration in Settings -> Service Settings -> Character Management and reconnect manually after.",
            _ => string.Empty
        };
    }

    private Vector4 GetUidColor()
    {
        return _apiController.ServerState switch
        {
            ServerState.Connecting => ImGuiColors.DalamudYellow,
            ServerState.Reconnecting => ImGuiColors.DalamudRed,
            ServerState.Connected => Colour.HexToVector4OrNull(_apiController.DisplayColour) ?? SnowcloakColours.OnlineBlue,
            ServerState.Disconnected => ImGuiColors.DalamudYellow,
            ServerState.Disconnecting => ImGuiColors.DalamudYellow,
            ServerState.Unauthorized => ImGuiColors.DalamudRed,
            ServerState.VersionMisMatch => ImGuiColors.DalamudRed,
            ServerState.Offline => ImGuiColors.DalamudRed,
            ServerState.RateLimited => ImGuiColors.DalamudYellow,
            ServerState.NoSecretKey => ImGuiColors.DalamudYellow,
            ServerState.MultiChara => ImGuiColors.DalamudYellow,
            _ => ImGuiColors.DalamudRed
        };
    }

    private Vector4? GetUidGlowColor()
    {
        if (_apiController.ServerState is not ServerState.Connected)
        {
            return null;
        }

        return Colour.HexToVector4OrNull(_apiController.DisplayGlowColour);
    }

    private string GetUidText()
    {
        return _apiController.ServerState switch
        {
            ServerState.Reconnecting => "Reconnecting",
            ServerState.Connecting => "Connecting",
            ServerState.Disconnected => "Disconnected",
            ServerState.Disconnecting => "Disconnecting",
            ServerState.Unauthorized => "Unauthorized",
            ServerState.VersionMisMatch => "Version mismatch",
            ServerState.Offline => "Unavailable",
            ServerState.RateLimited => "Rate Limited",
            ServerState.NoSecretKey => "No Secret Key",
            ServerState.MultiChara => "Duplicate Characters",
            ServerState.Connected => _apiController.DisplayName,
            _ => string.Empty
        };
    }
}
