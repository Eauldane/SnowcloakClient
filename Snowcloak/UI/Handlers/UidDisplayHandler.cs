using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.Utils;
using System.Numerics;

namespace Snowcloak.UI.Handlers;

public class UidDisplayHandler
{
    private readonly SnowcloakConfigService _snowcloakConfigService;
    private readonly SnowMediator _mediator;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly Dictionary<string, bool> _showUidForEntry = new(StringComparer.Ordinal);
    private string _editNickEntry = string.Empty;
    private string _editUserComment = string.Empty;
    private string _lastMouseOverUid = string.Empty;
    private bool _popupShown = false;
    private DateTime? _popupTime;

    public UidDisplayHandler(SnowMediator mediator, PairManager pairManager,
        ServerConfigurationManager serverManager, SnowcloakConfigService snowcloakConfigService)
    {
        _mediator = mediator;
        _pairManager = pairManager;
        _serverManager = serverManager;
        _snowcloakConfigService = snowcloakConfigService;
    }

    public void RenderPairList(IEnumerable<DrawPairBase> pairs)
    {
        var textHeight = ImGui.GetFontSize();
        var style = ImGui.GetStyle();
        var framePadding = style.FramePadding;
        var spacing = style.ItemSpacing;
        var lineHeight = textHeight + framePadding.Y * 2 + spacing.Y;
        var startY = ImGui.GetCursorStartPos().Y;
        var cursorY = ImGui.GetCursorPosY();
        var contentHeight = UiSharedService.GetWindowContentRegionHeight();

        foreach (var entry in pairs)
        {
            if ((startY + cursorY) < -lineHeight || (startY + cursorY) > contentHeight)
            {
                cursorY += lineHeight;
                ImGui.SetCursorPosY(cursorY);
                continue;
            }

            using (ImRaii.PushId(entry.ImGuiID)) entry.DrawPairedClient();
            cursorY += lineHeight;
        }
    }

    public void DrawPairText(string id, Pair pair, float textPosX, float originalY, Func<float> editBoxWidth)
    {
        ImGui.SameLine(textPosX);
        (bool textIsUid, string playerText) = GetPlayerText(pair);
        if (!string.Equals(_editNickEntry, pair.UserData.UID, StringComparison.Ordinal))
        {
            ImGui.SetCursorPosY(originalY);
            var pairColour = TryGetVanityColor(pair.UserData.DisplayColour);
            var pairGlowColour = TryGetVanityColor(pair.UserData.DisplayGlowColour);
            using (ImRaii.PushFont(UiBuilder.MonoFont, textIsUid))
            {
                DrawTextWithOptionalColor(playerText, pairColour, pairGlowColour);
            }
            if (ImGui.IsItemHovered())
            {
                if (!string.Equals(_lastMouseOverUid, id))
                {
                    _popupTime = DateTime.UtcNow.AddSeconds(_snowcloakConfigService.Current.ProfileDelay);
                }

                _lastMouseOverUid = id;

                if (_popupTime > DateTime.UtcNow || !_snowcloakConfigService.Current.ProfilesShow)
                {
                    ImGui.SetTooltip("Left click to switch between UID display and note" + Environment.NewLine
                        + "Right click to change note for " + pair.UserData.AliasOrUID + Environment.NewLine
                        + "Middle Mouse Button to open their profile in a separate window");
                }
                else if (_popupTime < DateTime.UtcNow && !_popupShown)
                {
                    _popupShown = true;
                    _mediator.Publish(new ProfilePopoutToggle(pair));
                }
            }
            else
            {
                if (string.Equals(_lastMouseOverUid, id))
                {
                    _mediator.Publish(new ProfilePopoutToggle(null));
                    _lastMouseOverUid = string.Empty;
                    _popupShown = false;
                }
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                var prevState = textIsUid;
                if (_showUidForEntry.ContainsKey(pair.UserData.UID))
                {
                    prevState = _showUidForEntry[pair.UserData.UID];
                }
                _showUidForEntry[pair.UserData.UID] = !prevState;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                var nickEntryPair = _pairManager.DirectPairs.Find(p => string.Equals(p.UserData.UID, _editNickEntry, StringComparison.Ordinal));
                nickEntryPair?.SetNote(_editUserComment);
                _editUserComment = pair.GetNote() ?? string.Empty;
                _editNickEntry = pair.UserData.UID;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Middle))
            {
                _mediator.Publish(new ProfileOpenStandaloneMessage(pair.UserData, pair));
            }
        }
        else
        {
            ImGui.SetCursorPosY(originalY);

            ImGui.SetNextItemWidth(editBoxWidth.Invoke());
            if (ImGui.InputTextWithHint("##" + pair.UserData.UID, "Nick/Notes", ref _editUserComment, 255, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                _serverManager.SetNoteForUid(pair.UserData.UID, _editUserComment);
                _serverManager.SaveNotes();
                _editNickEntry = string.Empty;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _editNickEntry = string.Empty;
            }
            UiSharedService.AttachToolTip("Hit ENTER to save\nRight click to cancel");
        }
    }

    public (bool isUid, string text) GetPlayerText(Pair pair)
    {
        var textIsUid = true;
        bool showUidInsteadOfName = ShowUidInsteadOfName(pair);
        string? playerText = _serverManager.GetNoteForUid(pair.UserData.UID);
        if (!showUidInsteadOfName && playerText != null)
        {
            if (string.IsNullOrEmpty(playerText))
            {
                playerText = pair.UserData.AliasOrUID;
            }
            else
            {
                textIsUid = false;
            }
        }
        else
        {
            playerText = pair.UserData.AliasOrUID;
        }

        if (_snowcloakConfigService.Current.ShowCharacterNames && textIsUid && !showUidInsteadOfName)
        {
            var name = pair.PlayerName;
            if (name != null)
            {
                playerText = name;
                textIsUid = false;
                var note = pair.GetNote();
                if (note != null)
                {
                    playerText = note;
                }
            }
        }

        return (textIsUid, playerText!);
    }

    internal void Clear()
    {
        _editNickEntry = string.Empty;
        _editUserComment = string.Empty;
    }

    internal void OpenProfile(Pair entry)
    {
        _mediator.Publish(new ProfileOpenStandaloneMessage(entry.UserData, entry));
    }

    internal void OpenAnalysis(Pair entry)
    {
        _mediator.Publish(new OpenPairAnalysisWindow(entry));
    }

    private bool ShowUidInsteadOfName(Pair pair)
    {
        _showUidForEntry.TryGetValue(pair.UserData.UID, out var showUidInsteadOfName);

        return showUidInsteadOfName;
    }
    private static Vector4? TryGetVanityColor(string? hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor)
            || hexColor.Length != 6
            || !int.TryParse(hexColor, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return ElezenTools.UI.Colour.HexToVector4(hexColor);
    }

    private static void DrawTextWithOptionalColor(string text, Vector4? color, Vector4? glowColor = null)
    {
        if (!color.HasValue && !glowColor.HasValue)
        {
            ImGui.TextUnformatted(text);
            return;
        }

        var foreground = color ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        if (glowColor.HasValue)
        {
            var drawList = ImGui.GetWindowDrawList();
            // Reserve the item using normal text layout so we get the exact baseline-aligned text position.
            ImGui.TextColored(Vector4.Zero, text);
            var textPos = ImGui.GetItemRectMin();
            var glow = glowColor.Value;
            var glowAlpha = Math.Clamp(glow.W <= 0f ? 0.45f : glow.W, 0.05f, 1f);
            var glowU32 = ImGui.ColorConvertFloat4ToU32(new Vector4(glow.X, glow.Y, glow.Z, glowAlpha));
            var spread = 1.0f * ImGuiHelpers.GlobalScale;
            drawList.AddText(new Vector2(textPos.X - spread, textPos.Y), glowU32, text);
            drawList.AddText(new Vector2(textPos.X + spread, textPos.Y), glowU32, text);
            drawList.AddText(new Vector2(textPos.X, textPos.Y - spread), glowU32, text);
            drawList.AddText(new Vector2(textPos.X, textPos.Y + spread), glowU32, text);
            drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(foreground), text);
            return;
        }

        ImGui.TextColored(foreground, text);
    }
}
