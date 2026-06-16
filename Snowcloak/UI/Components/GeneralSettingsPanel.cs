using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using ElezenTools.UI;
using Snowcloak.API.Data.Comparer;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.ServerConfiguration;

namespace Snowcloak.UI.Components;

public sealed class GeneralSettingsPanel
{
    private readonly SnowcloakConfigService _configService;
    private readonly NotesStore _notesStore;
    private readonly PairManager _pairManager;
    private readonly UiFontService _fontService;
    private bool? _notesSuccessfullyApplied;
    private bool _overwriteExistingLabels;

    public GeneralSettingsPanel(
        SnowcloakConfigService configService,
        NotesStore notesStore,
        PairManager pairManager,
        UiFontService fontService)
    {
        _configService = configService;
        _notesStore = notesStore;
        _pairManager = pairManager;
        _fontService = fontService;
    }

    public void Draw()
    {
        _fontService.BigText("Notes");
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.StickyNote, "Export all your user notes to clipboard"))
        {
            ImGui.SetClipboardText(NotesStore.ExportNotes(_pairManager.DirectPairs
                .UnionBy(_pairManager.GroupPairs.SelectMany(p => p.Value), p => p.UserData, UserDataComparer.Instance)
                .ToList()));
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileImport, "Import notes from clipboard"))
        {
            _notesSuccessfullyApplied = null;
            _notesSuccessfullyApplied = _notesStore.ApplyNotesFromClipboard(ImGui.GetClipboardText(), _overwriteExistingLabels);
        }

        ImGui.SameLine();
        ImGui.Checkbox("Overwrite existing notes", ref _overwriteExistingLabels);
        ElezenImgui.DrawHelpText("If this option is selected all already existing notes for UIDs will be overwritten by the imported notes.");
        if (_notesSuccessfullyApplied == true)
        {
            ElezenImgui.ColouredWrappedText("User Notes successfully imported", ImGuiColors.HealerGreen);
        }
        else if (_notesSuccessfullyApplied == false)
        {
            ElezenImgui.ColouredWrappedText("Attempt to import notes from clipboard failed. Check formatting and try again", ImGuiColors.DalamudRed);
        }

        var openPopupOnAddition = _configService.Current.OpenPopupOnAdd;
        if (ImGui.Checkbox("Open Notes Popup on user addition", ref openPopupOnAddition))
        {
            _configService.Update(c => c.OpenPopupOnAdd = openPopupOnAddition);
        }
        ElezenImgui.DrawHelpText("This will open a popup that allows you to set the notes for a user after successfully adding them to your individual pairs.");

        var autofillNotes = _configService.Current.AutofillEmptyNotesFromCharaName;
        if (ImGui.Checkbox("Automatically update empty notes with player names", ref autofillNotes))
        {
            _configService.Update(c => c.AutofillEmptyNotesFromCharaName = autofillNotes);
        }
        ElezenImgui.DrawHelpText("This will automatically set a user's note with their player name unless you override it");

        ImGui.Separator();
        _fontService.BigText("Venues");
        var autoJoinVenues = _configService.Current.AutoJoinVenueSyncshells;
        if (ImGui.Checkbox("Show prompts to join venue syncshells when on their grounds", ref autoJoinVenues))
        {
            _configService.Update(c => c.AutoJoinVenueSyncshells = autoJoinVenues);
        }
        ElezenImgui.DrawHelpText("Automatically detects venue housing plots and offers users an option to join them.");
    }
}
