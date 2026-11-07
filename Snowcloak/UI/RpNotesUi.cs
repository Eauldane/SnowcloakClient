using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using ElezenTools.UI;
using Snowcloak.API.Dto.Notes;
using Snowcloak.API.Notes;
using Snowcloak.API.Protocol;
using Snowcloak.Core.PlayerData;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;

namespace Snowcloak.UI;

public sealed class RpNotesUi : DisposableMediatorSubscriberBase
{
    private sealed record SearchLocation(Guid Card, Guid Perspective, string Author);
    private sealed record SearchResults(List<LocalRpNote> Notes, Dictionary<string, List<SearchLocation>> Locations);
    private sealed class Draft {
        public string Scope = "", Target = "";
        public RpDossier? Dossier;
        public List<RpOwnCharacter> Own = [];
        public bool Dirty;
        public DateTime Edited;
        public Task<bool>? Saving;
        public Guid ServerVersion;
        public Guid CharactersVersion;
    }
    private readonly RpNotesService _service;
    private readonly FileDialogManager _dialogs;
    private readonly NotesStore _existingNotes;
    private readonly SnowProfileManager _profiles;
    private readonly UiFontService _font;
    private readonly DalamudUtilService _dalamud;
    private readonly PairManager _pairs;
    private readonly Dictionary<string, Draft> _drafts = new(StringComparer.Ordinal);
    private Draft? _draft;
    private string _target = "", _search = "", _tag = "", _ownFilter = "", _theirFilter = "";
    private string _displayScope = "", _fallbackName = "";
    private Guid _card, _perspective;
    private OpenRpNotesMessage? _characterContext;
    private OpenRpNotesMessage? _pendingOpen;
    private bool _chooseContextCard;
    private Task<Dictionary<string, string>?>? _ownProfileLoad;
    private Dictionary<string, string>? _ownProfiles;
    private RpOwnCharacter? _selectedOwnCharacter;
    private string _newCharacterName = "";
    private string? _newPartnerIdent;
    private int _profileRefreshRequested;
    private bool _profileRefreshPending = true;
    private int _characterRefreshRequested;
    private bool _characterRefreshPending = true;
    private Task<CharacterIdentity>? _currentCharacterLoad;
    private CharacterIdentity _currentCharacter;
    private enum JournalSection { Characters, Plots, Scenes, FollowUps, Relationships, Knowledge, CustomFields, Coordination, Settings }
    private static readonly string[] JournalSections =
        ["Characters", "Plot threads", "Scene log", "Follow-ups", "Relationships", "IC knowledge", "Custom fields", "Player Notes", "Journal settings"];
    private JournalSection _journalSection = JournalSection.Plots;
    private bool _showResolvedPlots, _showCompletedFollowUps;
    private DateOnly _datePickerSelection;
    private bool _archived, _pinned, _followUps, _alphabetical, _showList = true, _includePending, _replaceImport;
    private bool _showOtherCharacters = true;
    private int _shown = 50;
    private long _indexedVersion = -1;
    private string _indexedQuery = "";
    private Task<SearchResults>? _searchTask;
    private Dictionary<string, List<SearchLocation>> _locations = new(StringComparer.Ordinal);
    private List<LocalRpNote> _results = [];
    private Task<RpNotesExport?>? _importTask;
    private RpNotesExport? _import;
    private RpNotesView? _importBaseline;
    private bool _confirmDelete, _confirmReset;
    private readonly HashSet<string> _importSelection = new(StringComparer.Ordinal);
    private int _importShown = 50;
    private string _notice = "";

    public RpNotesUi(ILogger<RpNotesUi> logger, SnowMediator mediator,
        RpNotesService service, FileDialogManager dialogs, NotesStore existingNotes, SnowProfileManager profiles,
        UiFontService font, DalamudUtilService dalamud, PairManager pairs) : base(logger, mediator)
    {
        _service = service; _dialogs = dialogs; _existingNotes = existingNotes;
        _profiles = profiles;
        _font = font;
        _dalamud = dalamud;
        _pairs = pairs;
        Mediator.Subscribe<ClearCharacterProfileDataMessage>(this, _ => RequestProfileRefresh());
        Mediator.Subscribe<ConnectedMessage>(this, _ => RequestCharacterRefresh());
        Mediator.Subscribe<SessionResumedMessage>(this, _ => RequestCharacterRefresh());
        Mediator.Subscribe<DalamudLoginMessage>(this, _ => RequestCharacterRefresh());
        Mediator.Subscribe<DalamudLogoutMessage>(this, _ => RequestCharacterRefresh());
        Mediator.Subscribe<ZoneSwitchEndMessage>(this, _ => RequestCharacterRefresh());
    }

    public bool OpenFor(OpenRpNotesMessage message)
    {
        if (!CanOpenJournal(message.TargetUid)) return false;
        _pendingOpen = message;
        RequestCharacterRefresh();
        return true;
    }

    private void ApplyOpenRequest(OpenRpNotesMessage message)
    {
        Flush(_draft);
        _target = message.TargetUid;
        _fallbackName = message.DisplayName;
        _card = _perspective = Guid.Empty;
        _characterContext = CharacterIdentityProtocol.IsDigest(message.CharacterIdent) ? message : null;
        _chooseContextCard = true;
        _selectedOwnCharacter = null;
        _journalSection = JournalSection.Plots;
        _draft = null;
        _confirmDelete = false;
        _showList = false;
        _service.Open(_target);
    }

    private bool CanOpenJournal(string uid) => _service.IsConnected
        ? _pairs.GetPairByUID(uid)?.IsMutualDirectPair == true
        : _service.HasRpNote(uid);

    private void RequestProfileRefresh() => Interlocked.Exchange(ref _profileRefreshRequested, 1);

    private void RequestCharacterRefresh()
    {
        RequestProfileRefresh();
        Interlocked.Exchange(ref _characterRefreshRequested, 1);
    }

    public void FlushChanges()
    {
        foreach (var draft in _drafts.Values) Flush(draft);
    }

    public bool TryClose()
    {
        UpdateSaves();
        FlushChanges();
        if (_drafts.Values.All(draft => !draft.Dirty && draft.Saving == null)) return true;
        _notice = "Saving journal...";
        return false;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        FlushChanges();
        foreach (var draft in _drafts.Values)
        {
            if (draft.Saving is { } saving)
            {
                if (!await saving.WaitAsync(cancellationToken).ConfigureAwait(false))
                    draft.Dirty = true;
                draft.Saving = null;
            }
            Flush(draft);
            if (draft.Saving is { } finalSave)
                await finalSave.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void UpdateSaves()
    {
        foreach (var draft in _drafts.Values) {
            if (draft.Saving?.IsCompleted == true) {
                if (!draft.Saving.IsCompletedSuccessfully || !draft.Saving.Result) {
                    draft.Dirty = true; _notice = "Local save failed. Retry or copy your notes.";
                }
                draft.Saving = null;
            }
            if (draft.Dirty && draft.Saving == null && DateTime.UtcNow - draft.Edited > TimeSpan.FromMilliseconds(750)) Flush(draft);
        }
    }

    public void Draw()
    {
        var view = _service.View;
        if (_displayScope != view.Scope) {
            if (_displayScope.Length > 0) { Flush(_draft); _draft = null; _target = ""; _fallbackName = ""; _card = _perspective = Guid.Empty; _characterContext = null; }
            _displayScope = view.Scope; _results.Clear(); _locations.Clear(); _indexedVersion = -1; _import = null; _importTask = null; _importBaseline = null;
            _ownProfileLoad = null; _ownProfiles = null; _selectedOwnCharacter = null;
            _currentCharacterLoad = null; _currentCharacter = default;
            RequestCharacterRefresh();
        }
        if (!_service.IsCurrent(view)) {
            _draft = null; _target = ""; _card = _perspective = Guid.Empty; _import = null; _characterContext = null;
            ImGui.TextUnformatted("Loading this account's notebook..."); return;
        }
        if (_draft != null && _draft.Scope != view.Scope) {
            Flush(_draft); _draft = null; _target = ""; _card = _perspective = Guid.Empty; _import = null; _characterContext = null;
        }
        if (_pendingOpen is { } request && view.Scope.Length > 0)
        {
            _pendingOpen = null;
            if (CanOpenJournal(request.TargetUid)) ApplyOpenRequest(request);
        }
        UpdateOwnProfiles(view);
        if (_notice.Length > 0) { ImGui.TextWrapped(_notice); if (ImGui.SmallButton("Dismiss")) _notice = ""; }
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Book, _showList ? "Back to journal" : "All journals")) _showList = !_showList;
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Sync, "Refresh journals and profiles")) {
            _service.Open(_target);
            RequestCharacterRefresh();
            if (_characterContext?.CharacterIdent is { } ident && _service.IsConnected)
                _ = _profiles.GetSnowProfileAsync(ident, forceRefresh: true);
        }
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Cog, "Journal options")) ImGui.OpenPopup("Notes files");
        DrawFiles(view);
        if (_importTask?.IsCompleted == true) {
            _import = _importTask.IsCompletedSuccessfully ? _importTask.Result : null; _importTask = null;
            if (_import != null && _importBaseline != null) {
                var existing = _importBaseline.Notes.Where(x => x.Record.Dossier != null).Select(x => x.Record.TargetUid).ToHashSet();
                _importSelection.Clear(); _importShown = 50; _replaceImport = false;
                foreach (var record in _import.Records.Where(x => !existing.Contains(x.TargetUid))) _importSelection.Add(record.TargetUid);
                ImGui.OpenPopup("Import detailed notes");
            }
        }
        DrawImport(view);
        ImGui.Separator();
        if (_showList)
        {
            using var list = ImRaii.Child("notebook-list", Vector2.Zero, true);
            if (list)
            {
                CharacterProfileUiShared.DrawSectionTitle("All journals");
                DrawList(view);
            }
            return;
        }
        using var editor = ImRaii.Child("dossier-detail", Vector2.Zero, false);
        if (editor) DrawEditor(view);
    }

    private void UpdateOwnProfiles(RpNotesView view)
    {
        if (Interlocked.Exchange(ref _profileRefreshRequested, 0) != 0)
            _profileRefreshPending = true;
        if (_ownProfileLoad?.IsCompleted == true)
        {
            if (!_profileRefreshPending && _ownProfileLoad.IsCompletedSuccessfully && _ownProfileLoad.Result is { } profiles)
                _ownProfiles = profiles;
            _ownProfileLoad = null;
        }
        if (_ownProfileLoad == null && _profileRefreshPending && view.Scope.Length > 0)
        {
            _profileRefreshPending = false;
            _ownProfileLoad = _service.LoadProfiles(view.Scope);
        }

        if (Interlocked.Exchange(ref _characterRefreshRequested, 0) != 0)
        {
            _currentCharacter = default;
            _characterRefreshPending = true;
        }
        if (_currentCharacterLoad?.IsCompleted == true)
        {
            if (!_characterRefreshPending && _currentCharacterLoad.IsCompletedSuccessfully)
                _currentCharacter = _currentCharacterLoad.Result;
            _currentCharacterLoad = null;
        }
        if (_currentCharacterLoad == null && _characterRefreshPending && view.Scope.Length > 0)
        {
            _characterRefreshPending = false;
            _currentCharacterLoad = _dalamud.GetCurrentCharacterIdentityAsync();
        }
    }

    private void DrawList(RpNotesView view)
    {
        var currentIdent = _currentCharacter.IsValid ? CharacterIdentityProtocol.FromContentId(_currentCharacter.ContentId) : null;
        var currentPerspective = currentIdent == null ? Guid.Empty : Guid.ParseExact(currentIdent[..32], "N");
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##search", "Search all RP fields", ref _search, 256);
        ImGui.Checkbox("Include archived", ref _archived); ImGui.Checkbox("Pinned only", ref _pinned);
        ImGui.Checkbox("Open follow-ups", ref _followUps); ImGui.Checkbox("Alphabetical", ref _alphabetical);
        var showOtherCharacters = currentPerspective == Guid.Empty || _showOtherCharacters;
        using (ImRaii.Disabled(currentPerspective == Guid.Empty))
            if (ImGui.Checkbox("Show other characters' journals", ref showOtherCharacters))
                _showOtherCharacters = showOtherCharacters;
        var currentOnly = !_showOtherCharacters && currentPerspective != Guid.Empty;
        ImGui.InputTextWithHint("##tag", "Tag filter", ref _tag, 128);
        ImGui.InputTextWithHint("##my", "My character filter", ref _ownFilter, 128);
        ImGui.InputTextWithHint("##their", "Their character filter", ref _theirFilter, 128);
        if (!view.Complete) ImGui.TextDisabled("Syncing with server...");
        var query = JsonSerializer.Serialize(new object[] { _search, _tag, _ownFilter, _theirFilter, _archived, _pinned,
            _followUps, _alphabetical, currentOnly, currentPerspective });
        if (_searchTask == null && (_indexedVersion != view.Revision || _indexedQuery != query)) {
            _indexedVersion = view.Revision; _indexedQuery = query;
            var terms = _search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var tag = _tag; var ownFilter = _ownFilter; var theirFilter = _theirFilter;
            var archived = _archived; var pinned = _pinned; var followUps = _followUps; var alphabetical = _alphabetical;
            var existingLabels = _existingNotes.GetNotesForServer(view.Service).UidServerComments;
            _searchTask = Task.Run(() => {
                var names = view.Metadata.OwnCharacters.ToDictionary(character => character.Id, character => character.Label);
                var selected = new List<LocalRpNote>();
                var locations = new Dictionary<string, List<SearchLocation>>(StringComparer.Ordinal);
                foreach (var note in view.Notes)
                {
                    var dossier = Effective(note);
                    if (dossier == null || (!archived && dossier.Archived) || (pinned && !dossier.Pinned)
                        || (tag.Length > 0 && !dossier.Tags.Any(t => t.Contains(tag, StringComparison.OrdinalIgnoreCase))))
                        continue;
                    var uid = note.Record.TargetUid;
                    var pairText = terms.Length == 0 ? "" : string.Join(" ", uid, existingLabels.GetValueOrDefault(uid),
                        dossier.DisplayName, dossier.Summary, dossier.Contact, dossier.PlayStyle, string.Join(" ", dossier.Tags));
                    var hits = new List<SearchLocation>();
                    foreach (var card in dossier.Characters)
                    {
                        if (!CardLabel(card).Contains(theirFilter, StringComparison.OrdinalIgnoreCase)) continue;
                        var cardText = terms.Length == 0 ? "" : string.Join(" ", pairText, CardLabel(card), card.Pronouns,
                            card.Aliases, card.Description, card.Background, card.Affiliations, card.StoryThreads, card.Hooks, card.Boundaries);
                        if (card.Perspectives.Count == 0 && ownFilter.Length == 0 && !followUps && Matches(cardText))
                            hits.Add(new(card.Id, Guid.Empty, ""));
                        foreach (var perspective in card.Perspectives.OrderByDescending(p => p.OwnCharacterId == currentPerspective))
                        {
                            var author = names.GetValueOrDefault(perspective.OwnCharacterId, "Unknown character");
                            if ((currentOnly && perspective.OwnCharacterId != currentPerspective)
                                || !author.Contains(ownFilter, StringComparison.OrdinalIgnoreCase)
                                || (followUps && !perspective.FollowUps.Any(f => !f.Done)))
                                continue;
                            if (terms.Length == 0 || Matches($"{cardText} {author} {JsonSerializer.Serialize(perspective)}"))
                                hits.Add(new(card.Id, perspective.OwnCharacterId, author));
                        }
                    }
                    if (hits.Count == 0 && !(dossier.Characters.Count == 0 && ownFilter.Length == 0 && theirFilter.Length == 0
                        && !followUps && Matches(pairText))) continue;
                    selected.Add(note);
                    locations[uid] = hits;
                }
                var notes = (alphabetical ? selected.OrderByDescending(x => Effective(x)!.Pinned).ThenBy(x => Effective(x)!.DisplayName, StringComparer.OrdinalIgnoreCase)
                    : selected.OrderByDescending(x => Effective(x)!.Pinned).ThenByDescending(x => x.Record.UpdatedAtUtc)).ToList();
                return new SearchResults(notes, locations);

                bool Matches(string text) => terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
            });
        }
        if (_searchTask?.IsCompleted == true) {
            if (_searchTask.IsCompletedSuccessfully && _indexedQuery == query && _indexedVersion == view.Revision)
            { _results = _searchTask.Result.Notes; _locations = _searchTask.Result.Locations; }
            _searchTask = null;
        }
        ImGui.Separator();
        var visibleResults = _results.Where(note => CanOpenJournal(note.Record.TargetUid)
            && (!currentOnly || _locations[note.Record.TargetUid].Count == 0
                || _locations[note.Record.TargetUid].Any(row => row.Perspective == Guid.Empty || row.Perspective == currentPerspective))).ToArray();
        foreach (var note in visibleResults.Take(_shown))
            DrawPairListing(view, note, currentPerspective, currentOnly);
        if (visibleResults.Length > _shown && ImGui.SmallButton("Show 50 more")) _shown += 50;
        if (visibleResults.Length == 0) ImGui.TextWrapped("No journals found. Start one from a direct pair's Detailed notes menu.");
    }

    private void DrawPairListing(RpNotesView view, LocalRpNote note, Guid currentPerspective, bool currentOnly)
    {
        var dossier = Effective(note)!;
        var uid = note.Record.TargetUid;
        var existingLabel = _existingNotes.GetNoteForUid(uid);
        var label = !string.IsNullOrWhiteSpace(existingLabel) ? existingLabel
            : string.IsNullOrWhiteSpace(dossier.DisplayName) ? uid : dossier.DisplayName;
        var title = dossier.Pinned ? $"{label} (Pinned)" : label;
        var count = dossier.Characters.Count;
        var expanded = false;
        var create = false;
        using var pairId = ImRaii.PushId(uid);
        ImGui.Spacing();
        using (var header = ImRaii.Table("pair-header", 2, ImGuiTableFlags.SizingStretchProp))
        {
            if (header)
            {
                const string newCharacter = "+ New character";
                ImGui.TableSetupColumn("Pair", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Add", ImGuiTableColumnFlags.WidthFixed,
                    ImGui.CalcTextSize(newCharacter).X + 2 * ImGui.GetStyle().FramePadding.X);
                ImGui.TableNextColumn();
                expanded = ImGui.TreeNodeEx($"{title} ({count} {(count == 1 ? "character" : "characters")})###characters",
                    ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.DefaultOpen);
                ImGui.TableNextColumn();
                using var disabled = ImRaii.Disabled(count >= 32);
                create = ImGui.Button(newCharacter, new(-1, 0));
            }
        }
        if (create)
        {
            _newCharacterName = "";
            ImGui.OpenPopup("New character");
        }
        DrawCharacterNamePopup("New character", $"Pair: {label}", name =>
        {
            var current = view.Notes.FirstOrDefault(item => item.Record.TargetUid == uid);
            if (!CanOpenJournal(uid) || current == null || Effective(current) == null)
            {
                _notice = "Journal unavailable. Refresh All Journals and try again.";
                return;
            }
            var draft = GetDraft(view, current);
            if (draft.Dossier == null || draft.Dossier.Characters.Count >= 32)
            {
                _notice = "Cannot add a character to this journal.";
                return;
            }
            OpenSavedJournal(uid, label, Guid.Empty, Guid.Empty);
            _draft = draft;
            AddPartnerCharacter(draft, name, null);
        }, "Create listing");
        if (!expanded) return;

        ImGui.Indent();
        if (count == 0 && ImGui.Selectable("Open pair journal", _target == uid))
            OpenSavedJournal(uid, label, Guid.Empty, Guid.Empty);
        foreach (var row in _locations[uid])
        {
            if (currentOnly && row.Perspective != Guid.Empty && row.Perspective != currentPerspective) continue;
            var card = dossier.Characters.First(card => card.Id == row.Card);
            using var cardId = ImRaii.PushId(card.Id.ToString());
            var cardLabel = CardLabel(card);
            var suffix = row.Perspective != Guid.Empty && row.Perspective != currentPerspective ? $"As {row.Author}" : "";
            var start = ImGui.GetCursorScreenPos();
            var height = ImGui.GetFrameHeight();
            if (ImGui.Selectable(cardLabel + "###" + row.Perspective,
                _target == uid && _card == card.Id && _perspective == row.Perspective,
                ImGuiSelectableFlags.None, new(0, height)))
                OpenSavedJournal(uid, label, card.Id, row.Perspective);
            if (suffix.Length > 0)
            {
                var offset = new Vector2(ImGui.CalcTextSize(cardLabel).X + ImGui.GetStyle().ItemSpacing.X, 0);
                ImGui.GetWindowDrawList().AddText(start + offset, ImGui.GetColorU32(ImGuiCol.TextDisabled), suffix);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(suffix.Length == 0 ? cardLabel : $"{cardLabel} ({suffix})");
        }
        ImGui.Unindent();
    }

    private void OpenSavedJournal(string target, string label, Guid card, Guid perspective)
    {
        if (!CanOpenJournal(target)) return;
        Flush(_draft);
        _target = target;
        _fallbackName = label;
        _draft = null;
        _characterContext = null;
        _selectedOwnCharacter = null;
        _perspective = perspective;
        SelectCard(card);
        _journalSection = JournalSection.Plots;
        _confirmDelete = false;
        _showList = false;
        _service.Open(target);
    }

    private static Guid DefaultPerspective(RpCharacterCard card, Guid preferred) =>
        preferred != Guid.Empty ? preferred : card.Perspectives.Count == 1 ? card.Perspectives[0].OwnCharacterId : Guid.Empty;

    private Draft GetDraft(RpNotesView view, LocalRpNote entry)
    {
        var target = entry.Record.TargetUid;
        var key = view.Scope + "|" + target;
        if (!_drafts.TryGetValue(key, out var draft))
        {
            draft = new() { Scope = view.Scope, Target = target };
            _drafts[key] = draft;
            LoadDraft(draft, entry, view);
        }
        else if (!draft.Dirty && draft.Saving == null && entry.Pending == null
            && (draft.ServerVersion != entry.Record.Version || draft.CharactersVersion != view.Metadata.CharactersVersion))
            LoadDraft(draft, entry, view);
        return draft;
    }

    private void DrawEditor(RpNotesView view)
    {
        if (_target.Length == 0) { ImGui.TextWrapped("Select a journal."); return; }
        if (!CanOpenJournal(_target))
        {
            ImGui.TextWrapped("Mutual direct pairs only.");
            return;
        }
        var entry = view.Notes.FirstOrDefault(x => x.Record.TargetUid == _target);
        var key = view.Scope + "|" + _target;
        if (entry == null) { ImGui.TextWrapped("Loading server copy..."); return; }
        var draft = _draft = GetDraft(view, entry);
        if (entry.Pending != null) ImGui.TextWrapped(entry.HasConflict ? "Conflict - review changes" : entry.Error.Length > 0 ? entry.Error : "Saved locally; waiting to sync");
        if (entry.Error.Length > 0 && ImGui.SmallButton("Retry sync")) _service.Retry(view.Scope, _target);
        if (entry.HasConflict) {
            if (ImGui.Button("Review server version")) ImGui.OpenPopup("Server conflict");
            if (ImGui.BeginPopupModal("Server conflict", ImGuiWindowFlags.AlwaysAutoResize)) {
                ImGui.TextWrapped("Choose which version to keep.");
                var serverText = JsonSerializer.Serialize(new { Dossier = entry.Conflict?.Dossier,
                    MyCharacters = entry.ConflictCharacters }, new JsonSerializerOptions { WriteIndented = true });
                ImGui.InputTextMultiline("Server copy", ref serverText, 1024 * 1024, new(600, 300), ImGuiInputTextFlags.ReadOnly);
                if (ImGui.Button("Keep mine")) {
                    if (draft.Dirty || draft.Saving != null) { Flush(draft); _notice = "Finish the local save before resolving the conflict."; }
                    else { _service.Resolve(view.Scope, _target, true); ImGui.CloseCurrentPopup(); }
                }
                ImGui.SameLine();
                if (ImGui.Button("Use server")) { draft.Dirty = false; _drafts.Remove(key); _draft = null; _service.Resolve(view.Scope, _target, false); ImGui.CloseCurrentPopup(); }
                ImGui.SameLine(); if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }
        if (draft.Dossier == null && entry.Pending != null)
        {
            ImGui.TextWrapped("Pending deletion.");
            return;
        }

        var targetProfile = ResolveTargetProfile(draft);
        var contextIdent = _characterContext?.CharacterIdent;
        var dossier = draft.Dossier;
        if (_chooseContextCard && dossier != null)
        {
            if (contextIdent == null)
                _card = dossier.Characters.Count == 1 ? dossier.Characters[0].Id : Guid.Empty;
            else
            {
                var matches = dossier.Characters.Where(card => card.CharacterIdent == contextIdent
                    || (targetProfile != null && card.CharacterIdent == targetProfile.Ident)).Take(2).ToArray();
                _card = matches.Length == 1 ? matches[0].Id : Guid.Empty;
            }
        }
        var selected = dossier?.Characters.FirstOrDefault(card => card.Id == _card);
        if (selected != null && _perspective == Guid.Empty)
            _perspective = DefaultPerspective(selected, Guid.Empty);
        var targetName = targetProfile != null ? ProfileName(targetProfile.Document.CharacterName, targetProfile.Ident) : null;
        var contextName = !string.IsNullOrWhiteSpace(_characterContext?.CharacterName) ? _characterContext.CharacterName : null;
        var isCurrentProfile = selected?.CharacterIdent != null && targetProfile != null
            && (selected.CharacterIdent == targetProfile.Ident || selected.CharacterIdent == contextIdent);
        var heading = isCurrentProfile ? targetName : selected != null ? CardLabel(selected) : targetName ?? contextName
            ?? (string.IsNullOrWhiteSpace(dossier?.DisplayName) ? _fallbackName : dossier.DisplayName);
        _font.BigText(string.IsNullOrWhiteSpace(heading) || heading == _target ? "Character journal" : heading);
        var own = _selectedOwnCharacter ?? draft.Own.FirstOrDefault(item => item.Id == _perspective);
        var status = view.Status.StartsWith("Offline", StringComparison.Ordinal) ? " (Offline)" : "";
        ImGui.TextDisabled(own == null ? $"Private RP journal{status}" : $"Writing as {own.Label}{status}");
        ImGui.Separator();

        var journal = selected?.Perspectives.FirstOrDefault(p => p.OwnCharacterId == _perspective);
        DrawWorkspace(view, draft, selected, targetProfile, journal);
    }

    private void DrawWorkspace(RpNotesView view, Draft draft, RpCharacterCard? selected,
        SnowProfileData? targetProfile, RpPerspective? journal)
    {
        var dossier = draft.Dossier;
        var section = _journalSection;
        if (journal == null && section is >= JournalSection.Plots and <= JournalSection.CustomFields)
            section = JournalSection.Characters;
        var scale = ImGuiHelpers.GlobalScale;
        var height = Math.Max(120f * scale, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        using var workspace = ImRaii.Table("journal-workspace", 2, ImGuiTableFlags.SizingStretchProp);
        if (!workspace) return;
        ImGui.TableSetupColumn("Sections", ImGuiTableColumnFlags.WidthFixed, 155f * scale);
        ImGui.TableSetupColumn("Editor", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextColumn();
        using (var navigation = ImRaii.Child("journal-navigation", new Vector2(0, height), true))
        {
            if (navigation)
            {
                for (var index = 0; index < JournalSections.Length; index++)
                {
                    var item = (JournalSection)index;
                    if (item is JournalSection.Plots or JournalSection.Coordination) ImGui.Separator();
                    var unavailable = item is >= JournalSection.Plots and <= JournalSection.CustomFields
                        ? journal == null : item >= JournalSection.Coordination && dossier == null;
                    using var disabled = ImRaii.Disabled(unavailable);
                    if (ImGui.Selectable(JournalSections[index], section == item))
                        section = _journalSection = item;
                }
                CharacterProfileUiShared.DrawSectionTitle("Saved characters");
                using (ImRaii.Disabled(dossier?.Characters.Count >= 32))
                {
                    if (ImGui.Button("+ New character", new(-1, 0)))
                    {
                        _newCharacterName = "";
                        ImGui.OpenPopup("New character");
                    }
                }
                DrawCharacterNamePopup("New character", "", name =>
                {
                    AddPartnerCharacter(draft, name, null);
                    _journalSection = JournalSection.Characters;
                }, "Create listing");
                if (dossier?.Characters is { Count: > 0 } characters)
                {
                    foreach (var character in characters)
                    {
                        var label = CardLabel(character);
                        if (ImGui.Selectable(label + "###" + character.Id, _card == character.Id))
                        {
                            SelectCard(character.Id);
                            _perspective = DefaultPerspective(character, _perspective);
                            if (_journalSection is JournalSection.Characters or >= JournalSection.Coordination)
                                _journalSection = JournalSection.Plots;
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
                    }
                }
            }
        }
        ImGui.TableNextColumn();
        using var targetId = ImRaii.PushId(draft.Target);
        using var editor = ImRaii.Child($"journal-section-{section}", new Vector2(0, height), true);
        if (!editor) return;
        CharacterProfileUiShared.DrawSectionTitle(JournalSections[(int)section]);
        if (section == JournalSection.Characters)
            DrawCharacterSetup(draft, selected, targetProfile);
        else if (section == JournalSection.Coordination && dossier != null)
            DrawPlayerCoordination(draft, dossier);
        else if (section == JournalSection.Settings && dossier != null)
            DrawReferenceDetails(view, draft, dossier, selected, targetProfile);
        else if (journal != null && selected != null)
        {
            using var cardId = ImRaii.PushId(selected.Id.ToString());
            using var perspectiveId = ImRaii.PushId(journal.OwnCharacterId.ToString());
            DrawPerspective(view, draft, journal, section);
        }
    }

    private void DrawCharacterSetup(Draft draft, RpCharacterCard? selected, SnowProfileData? targetProfile)
    {
        DrawOwnCharacterPicker(draft);
        ImGui.Spacing();
        ImGui.TextDisabled("Writing about");
        if (selected != null) ImGui.TextWrapped(CardLabel(selected));
        DrawPartnerChoices(draft, targetProfile);
        ImGui.Spacing();
        if (selected == null)
        {
            ImGui.TextWrapped("Select or add a character.");
        }
        else
        {
            using var characterId = ImRaii.PushId(selected.Id.ToString());
            DrawJournal(draft, selected);
        }
    }

    private SnowProfileData? ResolveTargetProfile(Draft draft)
    {
        if (_characterContext is not { CharacterIdent: { } ident } context || context.TargetUid != draft.Target)
            return null;
        var profile = _profiles.GetSnowProfile(ident);
        return profile.Revision > 0 && !profile.Disabled && profile.User?.UID == draft.Target
            && CharacterIdentityProtocol.IsDigest(profile.Ident) ? profile : null;
    }

    private static string ProfileName(string? name, string ident) =>
        string.IsNullOrWhiteSpace(name) ? $"Unnamed profile ({ident[..6]})" : name;

    private static string SavedProfileName(string? name, string ident)
        => ProfileName(SafeCharacterName(name), ident);

    private static string SafeCharacterName(string? name)
    {
        return string.Concat((name ?? "").EnumerateRunes()
            .Where(rune => !Rune.IsControl(rune) && rune.Value is not (0x2028 or 0x2029))
            .Take(128).Select(rune => rune.ToString())).Trim();
    }

    private void DrawOwnCharacterPicker(Draft draft)
    {
        var picked = _selectedOwnCharacter ?? draft.Own.FirstOrDefault(own => own.Id == _perspective);
        var currentIdent = _currentCharacter.IsValid
            ? CharacterIdentityProtocol.FromContentId(_currentCharacter.ContentId) : null;
        ImGui.TextDisabled("Writing as");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##my-character", picked?.Label ?? "Choose your character"))
        {
            var listed = new HashSet<Guid>();
            foreach (var (ident, name) in (_ownProfiles ?? []).OrderBy(entry => entry.Value, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!CharacterIdentityProtocol.IsDigest(ident))
                    continue;
                var id = Guid.ParseExact(ident[..32], "N");
                listed.Add(id);
                var label = ProfileName(name, ident);
                if (ImGui.Selectable(label + "###profile-" + id, _perspective == id))
                {
                    _selectedOwnCharacter = new RpOwnCharacter { Id = id, Label = SavedProfileName(name, ident) };
                    _perspective = id;
                }
            }
            if (currentIdent != null && _ownProfiles?.ContainsKey(currentIdent) != true)
            {
                var id = Guid.ParseExact(currentIdent[..32], "N");
                listed.Add(id);
                var name = SavedProfileName(_currentCharacter.Name, currentIdent);
                if (ImGui.Selectable($"{name} (current character)###profile-{id}", _perspective == id))
                {
                    _selectedOwnCharacter = new RpOwnCharacter { Id = id, Label = name };
                    _perspective = id;
                }
            }
            foreach (var own in draft.Own.Where(own => !listed.Contains(own.Id)))
            {
                if (ImGui.Selectable(own.Label + " (saved character)###saved-" + own.Id, _perspective == own.Id))
                {
                    _selectedOwnCharacter = null;
                    _perspective = own.Id;
                }
            }
            ImGui.EndCombo();
        }
        using (ImRaii.Disabled(draft.Own.Count >= 32))
        {
            if (ImGui.SmallButton("Add my character by name..."))
            {
                _newCharacterName = "";
                ImGui.OpenPopup("Add my character");
            }
        }
        DrawCharacterNamePopup("Add my character", "Saved when you start the journal.", name =>
        {
            _selectedOwnCharacter = new() { Id = Guid.NewGuid(), Label = name };
            _perspective = _selectedOwnCharacter.Id;
        });
    }

    private void DrawPartnerChoices(Draft draft, SnowProfileData? profile)
    {
        var contextIdent = _characterContext?.CharacterIdent;
        var ident = profile?.Ident ?? contextIdent;
        var activeAlreadySaved = ident != null && draft.Dossier?.Characters.Any(card =>
            card.CharacterIdent != null && (card.CharacterIdent == ident || card.CharacterIdent == contextIdent)) == true;
        var activeName = SafeCharacterName(profile?.Document.CharacterName);
        if (activeName.Length == 0) activeName = SafeCharacterName(_characterContext?.CharacterName);
        using (ImRaii.Disabled(draft.Dossier?.Characters.Count >= 32))
        {
            if (ident != null && !activeAlreadySaved)
            {
                if (activeName.Length > 0)
                {
                    if (ImGui.SmallButton($"Use {activeName}###use-active-character"))
                        AddPartnerCharacter(draft, activeName, ident);
                }
                else if (ImGui.SmallButton("Name their active character..."))
                {
                    _newCharacterName = "";
                    _newPartnerIdent = ident;
                    ImGui.OpenPopup("Add their character");
                }
            }
            if (ImGui.SmallButton("Add their character by name..."))
            {
                _newCharacterName = "";
                _newPartnerIdent = null;
                ImGui.OpenPopup("Add their character");
            }
        }
        DrawCharacterNamePopup("Add their character",
            _newPartnerIdent == null ? "" : "Private character name",
            name => AddPartnerCharacter(draft, name, _newPartnerIdent));
    }

    private void DrawCharacterNamePopup(string title, string help, Action<string> add, string action = "Use character")
    {
        if (!ImGui.BeginPopup(title)) return;
        if (help.Length > 0) ImGui.TextWrapped(help);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##character-name", "Character name", ref _newCharacterName, 513,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var name = SafeCharacterName(_newCharacterName);
        using (ImRaii.Disabled(name.Length == 0))
        {
            if (ImGui.Button(action) || (submitted && name.Length > 0))
            {
                add(name);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void AddPartnerCharacter(Draft draft, string name, string? ident)
    {
        if (draft.Dossier?.Characters.Count >= 32) return;
        var card = new RpCharacterCard
        {
            Label = name,
            FullName = name,
            World = ident == null ? "" : SafeCharacterName(_characterContext?.World),
            CharacterIdent = ident
        };
        draft.Dossier ??= new() { DisplayName = _fallbackName.Length > 0 ? _fallbackName : draft.Target };
        draft.Dossier.Characters.Add(card);
        SelectCard(card.Id);
        Changed(draft);
    }

    private void StartPerspective(Draft draft, RpCharacterCard card, RpOwnCharacter own)
    {
        if (draft.Own.All(item => item.Id != own.Id))
            draft.Own.Add(own);
        card.Perspectives.Add(new() { OwnCharacterId = own.Id });
        Changed(draft);
        _journalSection = JournalSection.Plots;
    }

    private void DrawReferenceDetails(RpNotesView view, Draft draft, RpDossier dossier, RpCharacterCard? selected, SnowProfileData? profile)
    {
        if (ImGui.Button("Save now")) Flush(draft);
        ImGui.SameLine();
        if (ImGui.Button("Copy journal data")) ImGui.SetClipboardText(JsonSerializer.Serialize(dossier, new JsonSerializerOptions { WriteIndented = true }));
        ImGui.SameLine();
        if (ImGui.Button("Export this journal..."))
        {
            var scope = view.Scope;
            var target = draft.Target;
            var pending = _includePending;
            _dialogs.SaveFileDialog("Export selected journal", ".json", "Snowcloak-journal", ".json", (ok, path) => {
                if (ok) _ = _service.Export(scope, path, pending, target);
            });
        }
        CharacterProfileUiShared.DrawSectionTitle("Notebook display");
        Fields(draft, dossier, ["DisplayName"]);
        var pinned = dossier.Pinned;
        if (ImGui.Checkbox("Pinned", ref pinned)) { dossier.Pinned = pinned; Changed(draft); }
        ImGui.SameLine();
        var archived = dossier.Archived;
        if (ImGui.Checkbox("Archived", ref archived)) { dossier.Archived = archived; Changed(draft); }
        var tags = string.Join(", ", dossier.Tags);
        ImGui.TextDisabled("Tags (comma separated)");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##tags", ref tags, 1024))
        {
            dossier.Tags = tags.Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Changed(draft);
        }

        if (selected != null)
        {
            CharacterProfileUiShared.DrawSectionTitle("Selected character");
            var contextIdent = _characterContext?.CharacterIdent;
            var activeIdent = profile?.Ident ?? contextIdent;
            if (selected.CharacterIdent == null && activeIdent != null
                && !dossier.Characters.Any(card => card.CharacterIdent != null
                    && (card.CharacterIdent == activeIdent || card.CharacterIdent == contextIdent))
                && ImGui.Button("Link to their active character"))
            {
                selected.CharacterIdent = activeIdent;
                Changed(draft);
            }
            if (selected.CharacterIdent != null && ImGui.Button("Unlink from game character"))
            {
                selected.CharacterIdent = null;
                Changed(draft);
            }
            if (ImGui.TreeNode("Saved character details"))
            {
                Fields(draft, selected, ["Label", "FullName", "World", "Pronouns", "Aliases", "Description",
                    "Background", "Affiliations", "StoryThreads", "Hooks", "Boundaries"]);
                ImGui.TreePop();
            }
            if (ImGui.Button("Delete this character journal...")) ImGui.OpenPopup("Delete character journal");
            if (ImGui.BeginPopupModal("Delete character journal", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextWrapped($"Delete all perspectives and entries for {CardLabel(selected)}?");
                if (ImGui.Button("Delete"))
                {
                    dossier.Characters.Remove(selected);
                    SelectCard(Guid.Empty);
                    Changed(draft);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        CharacterProfileUiShared.DrawSectionTitle("Delete journals");
        if (ImGui.Button("Delete all journals for this user...")) _confirmDelete = true;
        if (_confirmDelete)
        {
            ImGui.TextWrapped("Delete every character journal and reference note for this user?");
            if (ImGui.Button("Confirm deletion"))
            {
                draft.Dossier = null;
                Changed(draft);
                Flush(draft);
                _confirmDelete = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel deletion")) _confirmDelete = false;
        }
    }

    private void DrawPlayerCoordination(Draft draft, RpDossier dossier)
    {
        Fields(draft, dossier, ["Summary"]);
        if (!string.IsNullOrWhiteSpace(dossier.Contact) || !string.IsNullOrWhiteSpace(dossier.PlayStyle))
        {
            CharacterProfileUiShared.DrawSectionTitle("Saved player reference");
            Fields(draft, dossier, ["Contact", "PlayStyle"]);
        }
    }

    private static string CardLabel(RpCharacterCard card)
    {
        var name = card.FullName.Length == 0 || card.FullName == card.Label ? card.Label : $"{card.Label} ({card.FullName})";
        return card.World.Length == 0 ? name : $"{name} ({card.World})";
    }

    private void SelectCard(Guid cardId)
    {
        _card = cardId;
        _chooseContextCard = false;
    }

    private void DrawJournal(Draft draft, RpCharacterCard card)
    {
        var own = _selectedOwnCharacter ?? draft.Own.FirstOrDefault(item => item.Id == _perspective);
        if (own == null)
        {
            ImGui.TextWrapped("Select a character under Writing as.");
            return;
        }

        var journal = card.Perspectives.FirstOrDefault(p => p.OwnCharacterId == _perspective);
        if (journal != null)
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Book, "Open plot threads"))
                _journalSection = JournalSection.Plots;
        }
        else
        {
            using var disabled = ImRaii.Disabled(draft.Own.Count >= 32 && draft.Own.All(item => item.Id != own.Id));
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Start journal"))
            {
                StartPerspective(draft, card, own);
                return;
            }
            if (_selectedOwnCharacter != null && card.Perspectives.Count > 0
                && ImGui.BeginCombo("##move-journal", "Use an earlier journal for this character"))
            {
                foreach (var earlier in card.Perspectives)
                {
                    var previous = draft.Own.FirstOrDefault(item => item.Id == earlier.OwnCharacterId);
                    if (previous == null || !ImGui.Selectable($"Move {previous.Label}'s journal here###{previous.Id}"))
                        continue;
                    if (draft.Own.All(item => item.Id != own.Id))
                        draft.Own.Add(own);
                    earlier.OwnCharacterId = own.Id;
                    Changed(draft);
                    _journalSection = JournalSection.Plots;
                    break;
                }
                ImGui.EndCombo();
            }
        }
    }

    private void DrawPerspective(RpNotesView view, Draft draft, RpPerspective p, JournalSection section)
    {
        if (section == JournalSection.Plots) {
            DrawPlotThreads(draft, p);
        }
        if (section == JournalSection.Scenes) {
            DrawScenes(draft, p);
        }
        if (section == JournalSection.FollowUps) {
            DrawFollowUps(draft, p);
        }
        if (section == JournalSection.Knowledge) {
            Fields(draft, p, ["KnownName", "FirstMeeting", "Knowledge", "Secrets"]);
        }
        if (section == JournalSection.Relationships)
            DrawRelationships(view, draft, p);
        if (section == JournalSection.CustomFields) {
            DrawCustomFields(draft, p);
        }
    }

    private void DrawRelationships(RpNotesView view, Draft draft, RpPerspective perspective)
    {
        Fields(draft, perspective, ["Relationship"]);
        var relationship = perspective.RelationshipDetails ?? new RpRelationship();
        var changed = false;
        CharacterProfileUiShared.DrawSectionTitle("Relationship tags");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##relationship-tags", relationship.Tags.Count == 0 ? "Choose tags" : string.Join(", ", relationship.Tags.Select(RelationshipTagLabel))))
        {
            foreach (var tag in Enum.GetValues<RpRelationshipTag>())
            {
                var selected = relationship.Tags.Contains(tag);
                if (!ImGui.Checkbox(RelationshipTagLabel(tag), ref selected)) continue;
                if (selected) relationship.Tags.Add(tag);
                else relationship.Tags.Remove(tag);
                changed = true;
            }
            ImGui.EndCombo();
        }
        CharacterProfileUiShared.DrawSectionTitle("Sentiment");
        var trust = DrawSentiment("Trust", relationship.Trust, ["Unset", "Distrusts", "Wary", "Mixed", "Trusts", "Trusts deeply"]);
        var warmth = DrawSentiment("Warmth", relationship.Warmth, ["Unset", "Hostile", "Cool", "Neutral", "Warm", "Affectionate"]);
        changed |= trust != relationship.Trust || warmth != relationship.Warmth;
        relationship.Trust = trust;
        relationship.Warmth = warmth;

        CharacterProfileUiShared.DrawSectionTitle("IC contacts");
        using (ImRaii.Disabled(relationship.Contacts.Count >= RpNotesPolicy.MaxRelationshipContacts))
        {
            if (ImGui.Button("Add contact"))
            {
                relationship.Contacts.Add(new());
                changed = true;
            }
        }
        foreach (var contact in relationship.Contacts.ToArray())
        {
            using var id = ImRaii.PushId(contact.Id.ToString());
            var name = string.IsNullOrWhiteSpace(contact.Name) ? "New contact" : contact.Name;
            if (!ImGui.CollapsingHeader(name + "###contact", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            Fields(draft, contact, ["Name", "HowWeMet", "TheirConnection", "Standing"]);
            DrawContactLink(view, draft, contact);
            if (ImGui.SmallButton("Remove contact"))
            {
                relationship.Contacts.Remove(contact);
                changed = true;
            }
        }
        if (changed)
        {
            perspective.RelationshipDetails = relationship.Tags.Count > 0 || trust != null || warmth != null || relationship.Contacts.Count > 0
                ? relationship : null;
            Changed(draft);
        }
    }

    private static string RelationshipTagLabel(RpRelationshipTag tag) => tag switch
    {
        RpRelationshipTag.OwesMe => "Owes me",
        RpRelationshipTag.IOweThem => "I owe them",
        _ => tag.ToString()
    };

    private static RpSentiment? DrawSentiment(string label, RpSentiment? value, string[] steps)
    {
        var step = Math.Clamp((int?)value ?? 0, 0, steps.Length - 1);
        ImGui.TextDisabled(label);
        ImGui.SetNextItemWidth(-1);
        ImGui.SliderInt("##" + label, ref step, 0, steps.Length - 1, steps[step], ImGuiSliderFlags.AlwaysClamp);
        return step == 0 ? null : (RpSentiment)step;
    }

    private void DrawContactLink(RpNotesView view, Draft draft, RpRelationshipContact contact)
    {
        if (contact.JournalUid is { } uid)
        {
            var note = view.Notes.FirstOrDefault(note => note.Record.TargetUid == uid);
            var linked = note == null ? null : Effective(note)?.Characters.FirstOrDefault(card => card.Id == contact.JournalCharacterId);
            if (linked != null && CanOpenJournal(uid))
            {
                if (ImGui.SmallButton($"Open {CardLabel(linked)}'s journal###open-linked"))
                    OpenSavedJournal(uid, Effective(note!)!.DisplayName, linked.Id, DefaultPerspective(linked, _perspective));
            }
            else ImGui.TextDisabled("Linked journal is unavailable.");
            if (ImGui.SmallButton("Unlink journal"))
            {
                contact.JournalUid = null;
                contact.JournalCharacterId = null;
                Changed(draft);
            }
            return;
        }
        if (!ImGui.BeginCombo("##link-journal", "Link a saved journal (optional)")) return;
        foreach (var note in view.Notes)
        {
            if (!CanOpenJournal(note.Record.TargetUid) || Effective(note) is not { } dossier) continue;
            using var pairId = ImRaii.PushId(note.Record.TargetUid);
            foreach (var card in dossier.Characters)
            {
                if (!ImGui.Selectable($"{CardLabel(card)} - {dossier.DisplayName}###{card.Id}")) continue;
                contact.JournalUid = note.Record.TargetUid;
                contact.JournalCharacterId = card.Id;
                Changed(draft);
            }
        }
        ImGui.EndCombo();
    }

    private void DrawScenes(Draft draft, RpPerspective journal)
    {
        RpEncounter? added = null;
        if (ImGui.Button("Log a scene") && journal.Encounters.Count < 200)
        {
            added = new() { Date = JournalDateValue(DateOnly.FromDateTime(DateTime.Today)) };
            journal.Encounters.Insert(0, added);
            Changed(draft);
        }
        foreach (var scene in journal.Encounters.OrderByDescending(x => x.Date, StringComparer.Ordinal).ToArray())
        {
            using var id = ImRaii.PushId(scene.Id.ToString());
            if (scene == added) ImGui.SetNextItemOpen(true);
            var title = string.IsNullOrWhiteSpace(scene.Title) ? "Untitled scene" : scene.Title;
            if (!ImGui.CollapsingHeader($"{title} - {JournalDateLabel(scene.Date, "Undated")}###scene"))
                continue;
            Fields(draft, scene, ["Title", "Place"]);
            scene.Date = DrawJournalDate(draft, "Played on", scene.Date);
            Fields(draft, scene, ["Summary"]);
            if (ImGui.SmallButton("Remove scene")) { journal.Encounters.Remove(scene); Changed(draft); }
        }
        if (journal.Encounters.Count == 0) ImGui.TextDisabled("No scenes logged yet.");
        if (!string.IsNullOrWhiteSpace(journal.LatestScene) && ImGui.TreeNode("Earlier scene summary"))
        {
            Fields(draft, journal, ["LatestScene"]);
            ImGui.TreePop();
        }
    }

    private void DrawFollowUps(Draft draft, RpPerspective journal)
    {
        RpFollowUp? added = null;
        if (ImGui.Button("Add follow-up") && journal.FollowUps.Count < 100)
        {
            added = new();
            journal.FollowUps.Insert(0, added);
            Changed(draft);
        }
        ImGui.SameLine();
        var completed = journal.FollowUps.Count(x => x.Done);
        ImGui.Checkbox($"Show completed ({completed})###show-completed", ref _showCompletedFollowUps);
        if (journal.FollowUps.Count == completed && (!_showCompletedFollowUps || completed == 0))
            ImGui.TextDisabled("No open follow-ups.");
        foreach (var follow in journal.FollowUps.Where(x => !x.Done || _showCompletedFollowUps).OrderBy(x => x.Done).ToArray())
        {
            using var id = ImRaii.PushId(follow.Id.ToString());
            var done = follow.Done;
            if (ImGui.Checkbox("##done", ref done)) { follow.Done = done; Changed(draft); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(done ? "Mark incomplete" : "Mark complete");
            ImGui.SameLine();
            if (follow == added) ImGui.SetNextItemOpen(true);
            var title = follow.Text.Split('\n', 2)[0].Trim();
            if (title.Length > 80) title = title[..80] + "...";
            if (title.Length == 0) title = "New follow-up";
            if (follow.Date.Length > 0) title += $" - {JournalDateLabel(follow.Date, "")}";
            if (!ImGui.CollapsingHeader($"{title}###follow-up")) continue;
            Fields(draft, follow, ["Text"]);
            follow.Date = DrawJournalDate(draft, "Revisit on", follow.Date);
            if (ImGui.SmallButton("Remove follow-up")) { journal.FollowUps.Remove(follow); Changed(draft); }
        }
    }

    private string DrawJournalDate(Draft draft, string label, string value)
    {
        using var id = ImRaii.PushId(label);
        var today = DateOnly.FromDateTime(DateTime.Today);
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        if (ImGui.SmallButton($"{JournalDateLabel(value, "Choose date")}###date"))
        {
            _datePickerSelection = TryJournalDate(value, out var date) ? date : today;
            ImGui.OpenPopup("date-picker");
        }
        if (!ImGui.BeginPopup("date-picker")) return value;

        var year = _datePickerSelection.Year;
        var month = _datePickerSelection.Month;
        var day = _datePickerSelection.Day;

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Year", ref year);
        year = Math.Clamp(year, 1, 9999);
        var months = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;
        ImGui.SetNextItemWidth(120);
        if (ImGui.BeginCombo("Month", months[month - 1]))
        {
            for (var i = 1; i <= 12; i++)
                if (ImGui.Selectable(months[i - 1], i == month)) month = i;
            ImGui.EndCombo();
        }
        var days = DateTime.DaysInMonth(year, month);
        day = Math.Min(day, days);
        ImGui.SetNextItemWidth(120);
        if (ImGui.BeginCombo("Day", day.ToString(CultureInfo.CurrentCulture)))
        {
            for (var i = 1; i <= days; i++)
                if (ImGui.Selectable(i.ToString(CultureInfo.CurrentCulture), i == day)) day = i;
            ImGui.EndCombo();
        }
        _datePickerSelection = new(year, month, day);
        var next = value;
        if (ImGui.Button("Set date")) { next = JournalDateValue(_datePickerSelection); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Today")) { next = JournalDateValue(today); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Clear date")) { next = ""; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();

        if (next != value) Changed(draft);
        return next;
    }

    private static bool TryJournalDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string JournalDateValue(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string JournalDateLabel(string value, string emptyLabel) =>
        TryJournalDate(value, out var date) ? date.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : value.Length == 0 ? emptyLabel : value;

    private void DrawPlotThreads(Draft draft, RpPerspective journal)
    {
        RpPlotThread? added = null;
        if (ImGui.Button("Add plot thread") && (journal.PlotThreads?.Count ?? 0) < RpNotesPolicy.MaxPlotThreads)
        {
            added = new() { Title = "New thread" };
            (journal.PlotThreads ??= []).Insert(0, added);
            Changed(draft);
        }
        ImGui.SameLine();
        var resolved = journal.PlotThreads?.Count(x => x.Status == RpPlotStatus.Resolved) ?? 0;
        ImGui.Checkbox($"Show resolved ({resolved})###show-resolved", ref _showResolvedPlots);
        if ((journal.PlotThreads?.Count ?? 0) == resolved && (!_showResolvedPlots || resolved == 0))
            ImGui.TextDisabled("No open plot threads.");
        if (journal.PlotThreads is not { } threads)
            return;
        foreach (var thread in threads.Where(x => x.Status != RpPlotStatus.Resolved || _showResolvedPlots)
            .OrderBy(x => x.Status == RpPlotStatus.Resolved).ToArray())
        {
            using var id = ImRaii.PushId(thread.Id.ToString());
            if (thread == added) ImGui.SetNextItemOpen(true);
            if (!ImGui.CollapsingHeader($"{thread.Title} - {PlotStatusLabel(thread.Status)}###thread"))
                continue;
            Fields(draft, thread, ["Title"]);
            if (ImGui.BeginCombo("Status", PlotStatusLabel(thread.Status)))
            {
                foreach (var status in Enum.GetValues<RpPlotStatus>())
                    if (ImGui.Selectable(PlotStatusLabel(status), thread.Status == status))
                    {
                        thread.Status = status;
                        Changed(draft);
                    }
                ImGui.EndCombo();
            }
            Fields(draft, thread, ["Progress", "NextStep"]);
            if (ImGui.SmallButton("Remove plot thread"))
            {
                threads.Remove(thread);
                if (threads.Count == 0)
                    journal.PlotThreads = null;
                Changed(draft);
            }
        }
    }

    private static string PlotStatusLabel(RpPlotStatus status) => status switch
    {
        RpPlotStatus.Open => "Open hook",
        RpPlotStatus.InProgress => "In play",
        RpPlotStatus.Resolved => "Resolved",
        _ => status.ToString()
    };

    private void DrawCustomFields(Draft draft, RpPerspective journal)
    {
        if (ImGui.Button("Add custom field") && (journal.CustomFields?.Count ?? 0) < RpNotesPolicy.MaxCustomFields)
        {
            (journal.CustomFields ??= []).Add(new() { Label = "New field" });
            Changed(draft);
        }
        if (journal.CustomFields is not { } fields)
            return;
        foreach (var field in fields.ToArray())
        {
            using var id = ImRaii.PushId(field.Id.ToString());
            Fields(draft, field, ["Label", "Value"]);
            if (ImGui.SmallButton("Remove field"))
            {
                fields.Remove(field);
                if (fields.Count == 0)
                    journal.CustomFields = null;
                Changed(draft);
            }
            ImGui.Separator();
        }
    }

    private static readonly Dictionary<string, string> Labels = new() {
        ["DisplayName"] = "Notebook name", ["Contact"] = "Contact and availability", ["PlayStyle"] = "Play preferences",
        ["FullName"] = "Full name", ["StoryThreads"] = "Story threads", ["KnownName"] = "Known name",
        ["Relationship"] = "Relationship note", ["FirstMeeting"] = "First meeting",
        ["Name"] = "Contact name", ["HowWeMet"] = "How my character knows them",
        ["TheirConnection"] = "Connection to this character", ["Standing"] = "Current standing",
        ["Knowledge"] = "What my character knows", ["Secrets"] = "Suspicions and secrets",
        ["LatestScene"] = "Saved scene summary", ["Place"] = "Location",
        ["Progress"] = "Plot progress", ["NextStep"] = "Next step",
        ["Label"] = "Saved name", ["Value"] = "Note", ["Text"] = "Follow-up"
    };
    private void Fields(Draft draft, object value, string[] names)
    {
        foreach (var name in names) {
            var property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!;
            var text = (string)property.GetValue(value)!;
            var label = (value, name) switch
            {
                (RpEncounter, "Title") => "Scene title",
                (RpEncounter, "Summary") => "Scene summary",
                (RpPlotThread, "Title") => "Plot title",
                (RpDossier, "Summary") => "Coordination note",
                (RpCustomField, "Label") => "Field name",
                (RpCharacterCard, "Label") => "Private name for this saved character",
                _ => Labels.GetValueOrDefault(name, name)
            };
            var shortField = name is "Label" or "World" or "FullName" or "Pronouns" or "Aliases" or "DisplayName" or "KnownName" or "Title" or "Place" or "Name";
            ImGui.Spacing();
            ImGui.TextDisabled(label); ImGui.SetNextItemWidth(-1);
            var changed = shortField ? ImGui.InputText("##" + name, ref text, 4096)
                : ImGui.InputTextMultiline("##" + name, ref text, 131072, new(-1, name is "Summary" or "Knowledge" or "Background" ? 110 : 70));
            if (changed) { property.SetValue(value, text); Changed(draft); }
        }
    }

    private void DrawFiles(RpNotesView view)
    {
        if (!ImGui.BeginPopup("Notes files")) return;
        ImGui.TextWrapped(view.Status);
        if (_service.CachedOwners.Length > 0 && ImGui.BeginCombo("Offline notebook", view.Owner))
        {
            foreach (var owner in _service.CachedOwners)
                if (ImGui.Selectable(owner, view.Owner == owner)) _service.SelectOfflineOwner(owner);
            ImGui.EndCombo();
        }
        ImGui.TextWrapped("Server copies and backups are accessible to service operators. Local copies and exports are readable with OS access.");
        ImGui.Checkbox("Include pending local changes in export", ref _includePending);
        if (ImGui.Button("Export notebook...")) {
            var scope = view.Scope; var pending = _includePending;
            _dialogs.SaveFileDialog("Export detailed notes", ".json", "Snowcloak-detailed-notes", ".json", (ok, path) => {
                if (ok) _ = _service.Export(scope, path, pending);
            });
        }
        if (ImGui.Button("Import notebook...")) {
            _importBaseline = view; var scope = view.Scope;
            _dialogs.OpenFileDialog("Import detailed notes", ".json", (ok, paths) => {
                if (ok && paths.FirstOrDefault() is string path) _importTask = _service.ReadImport(scope, path);
            }, 1, null);
        }
        if (ImGui.Button("Clear downloaded copies (keep server notes)")) _ = _service.ClearCache(view.Scope);
        if (ImGui.Button("Delete entire server notebook...")) _confirmReset = true;
        if (_confirmReset) {
            ImGui.TextWrapped("Permanently delete all detailed dossiers and character perspectives on the server?");
            if (ImGui.Button("Confirm notebook deletion")) { _ = _service.Reset(view.Scope); _confirmReset = false; _draft = null; _drafts.Clear(); }
            if (ImGui.Button("Cancel")) _confirmReset = false;
        }
        ImGui.EndPopup();
    }

    private void DrawImport(RpNotesView view)
    {
        if (_import == null || _importBaseline == null || !ImGui.BeginPopupModal("Import detailed notes", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.TextUnformatted(_import.Records.Count + " dossiers, " + _import.OwnCharacters.Count + " own-character definitions");
        var existing = _importBaseline.Notes.Where(x => x.Record.Dossier != null).Select(x => x.Record.TargetUid).ToHashSet();
        ImGui.TextUnformatted(_import.Records.Count(x => !existing.Contains(x.TargetUid)) + " new; " + _import.Records.Count(x => existing.Contains(x.TargetUid)) + " existing");
        ImGui.Checkbox("Replace selected existing journals and perspectives", ref _replaceImport);
        ImGui.BeginChild("import-record-selection", new(600, 250), true);
        foreach (var record in _import.Records.Take(_importShown)) {
            using var id = ImRaii.PushId(record.TargetUid);
            var exists = existing.Contains(record.TargetUid);
            using var disabled = ImRaii.Disabled(exists && !_replaceImport);
            var selected = _importSelection.Contains(record.TargetUid) && (!exists || _replaceImport);
            var label = record.TargetUid + " - " + record.Dossier!.DisplayName + (exists ? " (existing)" : " (new)");
            if (ImGui.Checkbox(label, ref selected)) { if (selected) _importSelection.Add(record.TargetUid); else _importSelection.Remove(record.TargetUid); }
        }
        if (_import.Records.Count > _importShown && ImGui.SmallButton("Show 50 more records")) _importShown += 50;
        ImGui.EndChild();
        if (ImGui.Button("Import previewed selection") && view.Scope == _importBaseline.Scope) {
            var selection = RpNotesPolicy.Clone(_import);
            selection.Records = selection.Records.Where(x => _importSelection.Contains(x.TargetUid) && (_replaceImport || !existing.Contains(x.TargetUid))).ToList();
            _ = _service.Import(view.Scope, selection, _replaceImport, _importBaseline.Metadata.Generation,
                _importBaseline.Metadata.CharactersVersion, _importBaseline.Notes.ToDictionary(x => x.Record.TargetUid, x => x.Record.Version));
            _import = null; ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { _import = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private static RpDossier? Effective(LocalRpNote note) => note.HasNextDraft ? note.NextDraft :
        note.Pending != null ? note.Pending.Changes[0].Dossier : note.Record.Dossier;
    private static void LoadDraft(Draft draft, LocalRpNote entry, RpNotesView view)
    {
        draft.Dossier = RpNotesPolicy.Clone(Effective(entry));
        draft.Own = RpNotesPolicy.Clone(entry.NextCharacters ?? entry.Pending?.OwnCharacters ?? view.Metadata.OwnCharacters);
        draft.ServerVersion = entry.Record.Version;
        draft.CharactersVersion = view.Metadata.CharactersVersion;
    }
    private void Changed(Draft draft)
    {
        draft.Dirty = true;
        draft.Edited = DateTime.UtcNow;
        _chooseContextCard = false;
    }
    private void Flush(Draft? draft)
    {
        if (draft == null || !draft.Dirty || draft.Saving != null) return;
        draft.Dirty = false;
        draft.Saving = _service.Save(draft.Scope, draft.Target, RpNotesPolicy.Clone(draft.Dossier), RpNotesPolicy.Clone(draft.Own), draft.CharactersVersion);
    }
}
