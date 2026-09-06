using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data.Comparer;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.API.Dto.TemporaryAppearance;
using Snowcloak.API.Data.Enum;
using Snowcloak.WebAPI;

namespace Snowcloak.UI.Components;

public sealed class GeneralSettingsPanel
{
    private readonly SnowcloakConfigService _configService;
    private readonly NotesStore _notesStore;
    private readonly PairManager _pairManager;
    private readonly UiFontService _fontService;
    private readonly TemporaryPartyAppearanceService _temporaryParty;
    private readonly UserSafetyStore _userSafety;
    private readonly SnowMediator _mediator;
    private readonly ApiController _api;
    private bool? _notesSuccessfullyApplied;
    private bool _overwriteExistingLabels;

    public GeneralSettingsPanel(
        SnowcloakConfigService configService,
        NotesStore notesStore,
        PairManager pairManager,
        UiFontService fontService,
        TemporaryPartyAppearanceService temporaryParty,
        UserSafetyStore userSafety,
        SnowMediator mediator,
        ApiController api)
    {
        _configService = configService;
        _notesStore = notesStore;
        _pairManager = pairManager;
        _fontService = fontService;
        _temporaryParty = temporaryParty;
        _userSafety = userSafety;
        _mediator = mediator;
        _api = api;
    }

    public void Draw()
    {
        _fontService.BigText("Temporary party and alliance appearance");
        var enabled = _configService.Current.EnableTemporaryPartyAllianceAppearance;
        if (ImGui.Checkbox("Enable auto-party/alliance sync", ref enabled))
        {
            _configService.Update(config => config.EnableTemporaryPartyAllianceAppearance = enabled);
            if (!enabled)
                _ = _temporaryParty.StopAllAsync();
        }
        ElezenImgui.DrawHelpText("Auto-sync with party and alliance members who have Snowcloak and have enabled this setting.");
        if (_temporaryParty.ActivePeerCount > 0 && ImGui.Button("Stop temporary party/alliance sync now"))
        {
            _configService.Update(config => config.EnableTemporaryPartyAllianceAppearance = false);
            _ = _temporaryParty.StopAllAsync();
        }
        foreach (var pair in _pairManager.GetTemporaryAppearancePairs())
        {
            var grant = pair.TemporaryAppearance!.Grants.OrderByDescending(item => item.RemainingMs).FirstOrDefault();
            var sourceLabel = grant?.Source == TemporaryAppearanceSourceKind.Alliance ? "alliance" : "party";
            var remainingMs = grant == null ? 0 : _temporaryParty.GetRemainingLeaseMs(pair.UserData.UID, grant.Source);
            ImGui.PushID("temporary-party-" + pair.UserData.UID);
            ImGui.TextUnformatted($"{pair.GetNoteOrName() ?? pair.UserData.AliasOrUID} — {sourceLabel}, {remainingMs / 1000}s lease");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop"))
                _ = _temporaryParty.StopPeerAsync(pair.UserData.UID);
            ImGui.SameLine();
            using (ImRaii.Disabled(!_api.SupportsUnpairedUserReporting))
            {
                if (ImGui.SmallButton("Report"))
                    _mediator.Publish(new OpenReportPopupMessage(pair.UserData, pair.Ident,
                        ProfileVisibility.Public, 0, ProfileReportSurface.User));
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(!_userSafety.IsAvailable || _userSafety.IsBusy))
            {
                if (ImGui.SmallButton("Block"))
                    _userSafety.Block(pair.UserData.UID);
            }
            ImGui.PopID();
        }

        var inbound = _configService.Current.GlobalInboundAppearanceCategories;
        var allowAnimations = inbound.HasFlag(AppearanceCategoryMask.Animation);
        var allowSounds = inbound.HasFlag(AppearanceCategoryMask.Sound);
        var allowVfx = inbound.HasFlag(AppearanceCategoryMask.Vfx);
        var changedPolicy = ImGui.Checkbox("Allow synced animations", ref allowAnimations);
        changedPolicy |= ImGui.Checkbox("Allow synced sounds", ref allowSounds);
        changedPolicy |= ImGui.Checkbox("Allow synced VFX", ref allowVfx);
        if (changedPolicy)
        {
            SetCategory(ref inbound, AppearanceCategoryMask.Animation, allowAnimations);
            SetCategory(ref inbound, AppearanceCategoryMask.Sound, allowSounds);
            SetCategory(ref inbound, AppearanceCategoryMask.Vfx, allowVfx);
            _configService.Update(config => config.GlobalInboundAppearanceCategories = inbound);
            foreach (var pair in _pairManager.GetVisiblePairs())
                pair.ApplyLastReceivedData(forced: true);
        }

        ImGui.Separator();
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
        ElezenImgui.DrawHelpText("If this option is selected, all already existing notes for UIDs will be overwritten by the imported notes.");
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
        ElezenImgui.DrawHelpText("Pairs without a custom note set will auto-fill the first character name you see them online as.");

        ImGui.Separator();
        _fontService.BigText("Venues");
        var autoJoinVenues = _configService.Current.AutoJoinVenueSyncshells;
        if (ImGui.Checkbox("Show prompts to join venue syncshells when on their plots", ref autoJoinVenues))
        {
            _configService.Update(c => c.AutoJoinVenueSyncshells = autoJoinVenues);
        }
        ElezenImgui.DrawHelpText("Automatically detects venue housing plots and offers users an option to join them.");
    }

    private static void SetCategory(ref AppearanceCategoryMask mask, AppearanceCategoryMask category, bool enabled)
    {
        if (enabled) mask |= category;
        else mask &= ~category;
    }
}
