using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Core.Display;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
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
    private readonly NotesStore _notesStore;

    public UidDisplayHandler(SnowMediator mediator,
        NotesStore notesStore, SnowcloakConfigService snowcloakConfigService)
    {
        _mediator = mediator;
        _notesStore = notesStore;
        _snowcloakConfigService = snowcloakConfigService;
    }

    public static void RenderPairList(IEnumerable<DrawPairBase> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        // Index into the list directly so the clipper can skip off-screen rows entirely.
        // Materialise lazy sequences (e.g. LINQ filters) since the clipper needs random access.
        var list = pairs as IReadOnlyList<DrawPairBase> ?? pairs.ToList();
        if (list.Count == 0)
        {
            return;
        }

        // Pair rows are uniform height, so an ImGuiListClipper can render only the visible
        // range instead of walking every member each frame (matters for 200+ member shells).
        var clipper = new ImGuiListClipper();
        clipper.Begin(list.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var entry = list[i];
                using (ImRaii.PushId(entry.ImGuiID)) entry.DrawPairedClient();
            }
        }
        clipper.End();
    }

    internal float DrawPairText(string id, Pair pair, float textPosX, float textPosY, Func<float> editBoxWidth, DrawPairBase.PairRowTextState state)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(editBoxWidth);
        ArgumentNullException.ThrowIfNull(state);

        ImGui.SameLine(textPosX);
        (bool textIsUid, string playerText) = GetPlayerText(pair, state.ShowUidInsteadOfName);
        if (!state.EditingNote)
        {
            ImGui.SetCursorPosY(textPosY);
            var options = PairDisplayDecorationMapper.CreateOptions(
                _snowcloakConfigService.Current,
                inRestrictedContent: false,
                // Nameplate colours only apply in-game; the pair list keeps the default
                // text colour and only deviates for an explicit vanity colour.
                usePairColours: false,
                usePairingHighlights: false);
            var decoration = PairDisplayDecorationPolicy.Resolve(
                options,
                PairDisplayDecorationMapper.CreatePairSubject(pair, allowPairVanity: true));
            var (pairColour, pairGlowColour) = PairDisplayDecorationMapper.ToVectorColours(decoration);
            using (ImRaii.PushFont(UiBuilder.MonoFont, textIsUid))
            {
                ElezenImgui.ColouredText(playerText, pairColour, pairGlowColour);
            }
            if (ImGui.IsItemHovered())
            {
                if (!string.Equals(state.LastMouseOverUid, id, StringComparison.Ordinal))
                {
                    state.PopupTime = DateTime.UtcNow.AddSeconds(_snowcloakConfigService.Current.ProfileDelay);
                }

                state.LastMouseOverUid = id;

                if (state.PopupTime > DateTime.UtcNow || !_snowcloakConfigService.Current.ProfilesShow)
                {
                    ImGui.SetTooltip("Left click to switch between UID display and note" + Environment.NewLine
                        + "Right click to change note for " + pair.UserData.AliasOrUID + Environment.NewLine
                        + "Middle Mouse Button to open their profile in a separate window");
                }
                else if (state.PopupTime < DateTime.UtcNow && !state.PopupShown)
                {
                    state.PopupShown = true;
                    _mediator.Publish(new ProfilePopoutToggle(pair));
                }
            }
            else
            {
                if (string.Equals(state.LastMouseOverUid, id, StringComparison.Ordinal))
                {
                    _mediator.Publish(new ProfilePopoutToggle(null));
                    state.LastMouseOverUid = string.Empty;
                    state.PopupShown = false;
                }
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                state.ShowUidInsteadOfName = !state.ShowUidInsteadOfName;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                state.EditUserComment = pair.GetNote() ?? string.Empty;
                state.EditingNote = true;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Middle))
            {
                _mediator.Publish(new ProfileOpenStandaloneMessage(pair.UserData, pair, FallbackName: pair.PlayerName));
            }
        }
        else
        {
            ImGui.SetCursorPosY(textPosY);

            ImGui.SetNextItemWidth(editBoxWidth.Invoke());
            var editUserComment = state.EditUserComment;
            if (ImGui.InputTextWithHint("##" + pair.UserData.UID, "Nick/Notes", ref editUserComment, 255, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                state.EditUserComment = editUserComment;
                _notesStore.SetNoteForUid(pair.UserData.UID, state.EditUserComment);
                _notesStore.Save();
                state.EditingNote = false;
            }
            else
            {
                state.EditUserComment = editUserComment;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                state.EditingNote = false;
            }
            ElezenImgui.AttachTooltip("Hit ENTER to save\nRight click to cancel");
        }

        return ImGui.GetItemRectMax().X;
    }

    public (bool isUid, string text) GetPlayerText(Pair pair)
        => GetPlayerText(pair, showUidInsteadOfName: false);

    private (bool isUid, string text) GetPlayerText(Pair pair, bool showUidInsteadOfName)
    {
        ArgumentNullException.ThrowIfNull(pair);

        var textIsUid = true;
        string? playerText = _notesStore.GetNoteForUid(pair.UserData.UID);
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

    internal void OpenProfile(Pair entry)
    {
        _mediator.Publish(new ProfileOpenStandaloneMessage(entry.UserData, entry, FallbackName: entry.PlayerName));
    }

    internal void OpenAnalysis(Pair entry)
    {
        _mediator.Publish(new OpenPairAnalysisWindow(entry));
    }
}
