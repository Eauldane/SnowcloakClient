using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using System.Globalization;
using System.Numerics;
using System.Threading;

namespace Snowcloak.UI;

internal sealed class CharaDataHubMcdOnlineTab
{
    private const int MaxPoses = 10;
    private const float LabelColumnWidth = 200f;

    private readonly CharaDataHubContext _ctx;
    private readonly CharaDataManager _charaDataManager;
    private readonly CharaDataConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly NotesStore _notesStore;
    private readonly PairManager _pairManager;
    private readonly CancellationToken _disposalToken;

    private string _selectedSpecificUserIndividual = string.Empty;
    private string _selectedSpecificGroupIndividual = string.Empty;
    private string _specificIndividualAdd = string.Empty;
    private string _specificGroupAdd = string.Empty;
    private bool _selectNewEntry;
    private int _dataEntries;

    public CharaDataHubMcdOnlineTab(
        CharaDataHubContext ctx,
        CharaDataManager charaDataManager,
        CharaDataConfigService configService,
        DalamudUtilService dalamudUtilService,
        NotesStore notesStore,
        PairManager pairManager,
        CancellationToken disposalToken)
    {
        _ctx = ctx;
        _charaDataManager = charaDataManager;
        _configService = configService;
        _dalamudUtilService = dalamudUtilService;
        _notesStore = notesStore;
        _pairManager = pairManager;
        _disposalToken = disposalToken;
    }

    public void Draw()
    {
        ModernSection.Header(FontAwesomeIcon.Cloud, "Online Character Data");
        CharaDataHubWidgets.DrawHelpFoldout(_configService, BuildIntroText());
        ImGuiHelpers.ScaledDummy(5);

        DrawRefreshButton();
        DrawEntryTable();
        DrawCreateControls();
        DrawCreateStatus();

        ImGuiHelpers.ScaledDummy(10);
        ImGui.Separator();

        SelectNewestEntryAfterCreation();
        _ = _charaDataManager.OwnCharaData.TryGetValue(_ctx.SelectedDtoId, out var selected);
        DrawEditor(selected);
    }

    private static string BuildIntroText()
    {
        return "In this tab you can create, view and edit your own Character Data that is stored on the server."
            + Environment.NewLine + Environment.NewLine
            + "Character Data Online functions similar to the previous MCDF standard for exporting your character, except that you do not have to send a file to the other person but solely a code."
            + Environment.NewLine + Environment.NewLine
            + "There would be a bit too much to explain here on what you can do here in its entirety, however, all elements in this tab have help texts attached what they are used for. Please review them carefully."
            + Environment.NewLine + Environment.NewLine
            + "Be mindful that when you share your Character Data with other people there is a chance that, with the help of unsanctioned 3rd party plugins, your appearance could be stolen irreversibly, just like when using MCDF.";
    }

    private void DrawRefreshButton()
    {
        using (ImRaii.Disabled(_charaDataManager.OwnDataDownloading || _charaDataManager.OwnDataOnCooldown))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowCircleDown, "Download your Online Character Data from Server"))
            {
                _ = _charaDataManager.GetAllData(_disposalToken);
            }
        }

        if (_charaDataManager.OwnDataOnCooldown)
        {
            ElezenImgui.AttachTooltip("You can only refresh all character data from server every minute. Please wait.");
        }
    }

    private void DrawEntryTable()
    {
        var width = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
        using var table = ImRaii.Table("Own Character Data", 12,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY,
            new Vector2(width, 110));
        if (!table)
        {
            return;
        }

        SetupEntryTableColumns();
        foreach (var entry in _charaDataManager.OwnCharaData.Values.OrderBy(static item => item.CreatedDate))
        {
            DrawEntryRow(entry);
        }
    }

    private static void SetupEntryTableColumns()
    {
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 18);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 18);
        ImGui.TableSetupColumn("Code");
        ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Created");
        ImGui.TableSetupColumn("Updated");
        ImGui.TableSetupColumn("Download Count", ImGuiTableColumnFlags.WidthFixed, 18);
        ImGui.TableSetupColumn("Downloadable", ImGuiTableColumnFlags.WidthFixed, 18);
        ImGui.TableSetupColumn("Files", ImGuiTableColumnFlags.WidthFixed, 32);
        ImGui.TableSetupColumn("Glamourer", ImGuiTableColumnFlags.WidthFixed, 18);
        ImGui.TableSetupColumn("Customize+", ImGuiTableColumnFlags.WidthFixed, 18);
        ImGui.TableSetupColumn("Expires", ImGuiTableColumnFlags.WidthFixed, 18);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
    }

    private void DrawEntryRow(CharaDataFullExtendedDto entry)
    {
        var updateDto = _charaDataManager.GetUpdateDto(entry.Id);
        TableCell(() =>
        {
            if (string.Equals(entry.Id, _ctx.SelectedDtoId, StringComparison.Ordinal))
            {
                ElezenImgui.ShowIcon(FontAwesomeIcon.CaretRight);
            }
        });
        TableCell(() => _ctx.DrawAddOrRemoveFavorite(entry));
        TableCell(() => DrawSelectableCode(entry, updateDto));
        TableCell(() => DrawSelectableText(entry.Id, entry.Description, entry.Description));
        TableCell(() => DrawSelectableText(entry.Id, entry.CreatedDate.ToLocalTime().ToString(CultureInfo.CurrentCulture)));
        TableCell(() => DrawSelectableText(entry.Id, entry.UpdatedDate.ToLocalTime().ToString(CultureInfo.CurrentCulture)));
        TableCell(() => DrawSelectableText(entry.Id, entry.DownloadCount.ToString(CultureInfo.InvariantCulture)));
        TableCell(() => DrawDownloadableIcon(entry));
        TableCell(() => DrawFilesCell(entry));
        TableCell(() => DrawDataPresenceCell(entry.Id, !string.IsNullOrEmpty(entry.GlamourerData), "Glamourer"));
        TableCell(() => DrawDataPresenceCell(entry.Id, !string.IsNullOrEmpty(entry.CustomizeData), "Customize+"));
        TableCell(() => DrawExpiryCell(entry));
    }

    private static void TableCell(Action draw)
    {
        ImGui.TableNextColumn();
        draw();
    }

    private void DrawSelectableCode(CharaDataFullExtendedDto entry, CharaDataExtendedUpdateDto? updateDto)
    {
        if (updateDto?.HasChanges == true)
        {
            ElezenImgui.ColouredText(entry.FullId, ImGuiColors.DalamudYellow);
            ElezenImgui.AttachTooltip("This entry has unsaved changes");
        }
        else
        {
            ImGui.TextUnformatted(entry.FullId);
        }

        SelectEntryOnClick(entry.Id);
    }

    private void DrawSelectableText(string entryId, string text, string? tooltip = null)
    {
        ImGui.TextUnformatted(text);
        SelectEntryOnClick(entryId);
        if (!string.IsNullOrEmpty(tooltip))
        {
            ElezenImgui.AttachTooltip(tooltip);
        }
    }

    private void DrawDownloadableIcon(CharaDataFullExtendedDto entry)
    {
        var isDownloadable = !entry.HasMissingFiles && !string.IsNullOrEmpty(entry.GlamourerData);
        ElezenImgui.GetBooleanIcon(isDownloadable, false);
        SelectEntryOnClick(entry.Id);
        ElezenImgui.AttachTooltip(isDownloadable
            ? "Can be downloaded by others"
            : "Cannot be downloaded: Has missing files or data, please review this entry manually");
    }

    private void DrawFilesCell(CharaDataFullExtendedDto entry)
    {
        var count = entry.FileGamePaths.Concat(entry.FileSwaps ?? []).Count();
        ImGui.TextUnformatted(count.ToString(CultureInfo.InvariantCulture));
        SelectEntryOnClick(entry.Id);
        ElezenImgui.AttachTooltip(count == 0 ? "No File data attached" : "Has File data attached");
    }

    private void DrawDataPresenceCell(string entryId, bool present, string label)
    {
        ElezenImgui.GetBooleanIcon(present, false);
        SelectEntryOnClick(entryId);
        ElezenImgui.AttachTooltip(present ? $"Has {label} data attached" : $"No {label} data attached");
    }

    private void DrawExpiryCell(CharaDataFullExtendedDto entry)
    {
        var expires = !Equals(DateTime.MaxValue, entry.ExpiryDate);
        ElezenImgui.ShowIcon(expires ? FontAwesomeIcon.Clock : FontAwesomeIcon.None, ImGuiColors.DalamudYellow);
        SelectEntryOnClick(entry.Id);
        if (expires)
        {
        ElezenImgui.AttachTooltip($"This entry will expire on {entry.ExpiryDate.ToLocalTime().ToString(CultureInfo.CurrentCulture)}");
        }
    }

    private void SelectEntryOnClick(string entryId)
    {
        if (ImGui.IsItemClicked())
        {
            _ctx.SelectedDtoId = entryId;
        }
    }

    private void DrawCreateControls()
    {
        using (ImRaii.Disabled(!_charaDataManager.Initialized
            || !_charaDataManager.DataCreation.IsIdle
            || _charaDataManager.OwnCharaData.Count == _charaDataManager.MaxCreatableCharaData))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "New Character Data Entry"))
            {
                _charaDataManager.CreateCharaDataEntry(_ctx.ClosalToken);
                _selectNewEntry = true;
            }
        }

        if (!_charaDataManager.DataCreation.IsIdle)
        {
            ElezenImgui.AttachTooltip("You can only create new character data every few seconds. Please wait.");
        }
        if (!_charaDataManager.Initialized)
        {
            ElezenImgui.AttachTooltip("Please use the button \"Get Own Chara Data\" once before you can add new data entries.");
        }

        DrawEntryCount();
    }

    private void DrawEntryCount()
    {
        if (!_charaDataManager.Initialized)
        {
            return;
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ElezenImgui.WrappedText($"Chara Data Entries on Server: {_charaDataManager.OwnCharaData.Count}/{_charaDataManager.MaxCreatableCharaData}");
        if (_charaDataManager.OwnCharaData.Count == _charaDataManager.MaxCreatableCharaData)
        {
            ImGui.AlignTextToFramePadding();
            ElezenImgui.ColouredWrappedText("You have reached the maximum Character Data entries and cannot create more.", ImGuiColors.DalamudYellow);
        }
    }

    private void DrawCreateStatus()
    {
        if (_charaDataManager.DataCreation.IsRunning)
        {
            ElezenImgui.ColouredWrappedText("Creating new character data entry on server...", ImGuiColors.DalamudYellow);
        }
        else if (_charaDataManager.DataCreation.IsCompleted)
        {
            var result = _charaDataManager.DataCreation.Result;
            ElezenImgui.ColouredWrappedText(result.Output ?? string.Empty,
                result.Success ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
        }
    }

    private void SelectNewestEntryAfterCreation()
    {
        var currentEntries = _charaDataManager.OwnCharaData.Count;
        if (_selectNewEntry && currentEntries != _dataEntries && _charaDataManager.OwnCharaData.Count != 0)
        {
            _ctx.SelectedDtoId = _charaDataManager.OwnCharaData
                .OrderBy(static item => item.Value.CreatedDate)
                .Last()
                .Value
                .Id;
            _selectNewEntry = false;
        }

        _dataEntries = currentEntries;
    }

    private void DrawEditor(CharaDataFullExtendedDto? dataDto)
    {
        using var imguiId = ImRaii.PushId(dataDto?.Id ?? "NoData");
        if (dataDto == null)
        {
            ImGuiHelpers.ScaledDummy(5);
            ElezenImgui.DrawGroupedCenteredColorText("Select an entry above to edit its data.", ImGuiColors.DalamudYellow);
            return;
        }

        var updateDto = _charaDataManager.GetUpdateDto(dataDto.Id);
        if (updateDto == null)
        {
            ElezenImgui.DrawGroupedCenteredColorText("Something went awfully wrong and there's no update DTO. Try updating Character Data via the button above.", ImGuiColors.DalamudYellow);
            return;
        }

        DrawSavePanel(dataDto, updateDto);
        using var child = ImRaii.Child("editChild", new Vector2(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
        DrawGeneralSection(dataDto, updateDto);
        SectionGap();
        DrawAccessSection(updateDto);
        SectionGap();
        DrawAppearanceSection(dataDto, updateDto);
        SectionGap();
        DrawPoseSection(updateDto);
    }

    private void DrawSavePanel(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        var hasPendingUpdate = updateDto.HasChanges || !_charaDataManager.CharaUpdate.IsIdle;
        if (!hasPendingUpdate && _charaDataManager.Upload.IsIdle)
        {
            return;
        }

        ImGuiHelpers.ScaledDummy(5);
        using var indent = ImRaii.PushIndent(10f);
        ElezenImgui.DrawGrouped(() =>
        {
            if (updateDto.HasChanges)
            {
                ImGui.AlignTextToFramePadding();
                ElezenImgui.ColouredWrappedText("Warning: You have unsaved changes!", ImGuiColors.DalamudRed);
                ImGui.SameLine();
                DrawSaveActions(dataDto, updateDto);
            }

            DrawUpdateStatus();
            DrawUploadStatus();
        });
        ImGuiHelpers.ScaledDummy(5);
    }

    private void DrawSaveActions(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        using var disabled = ImRaii.Disabled(_charaDataManager.CharaUpdate.IsRunning);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowCircleUp, "Save to Server"))
        {
            _charaDataManager.UploadCharaData(dataDto.Id);
        }
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Undo, "Undo all changes"))
        {
            updateDto.UndoChanges();
        }
    }

    private void DrawUpdateStatus()
    {
        if (_charaDataManager.CharaUpdate.IsRunning)
        {
            ElezenImgui.ColouredWrappedText("Updating data on server, please wait.", ImGuiColors.DalamudYellow);
        }
    }

    private void DrawUploadStatus()
    {
        if (_charaDataManager.Upload.IsRunning)
        {
            _ctx.DisableDisabled(() =>
            {
                if (_charaDataManager.UploadProgress != null)
                {
                    ElezenImgui.ColouredWrappedText(_charaDataManager.UploadProgress.Value ?? string.Empty, ImGuiColors.DalamudYellow);
                }
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Ban, "Cancel Upload"))
                {
                    _charaDataManager.CancelUpload();
                }
            });
            return;
        }

        if (_charaDataManager.Upload.IsCompleted)
        {
            var result = _charaDataManager.Upload.Result;
            ElezenImgui.ColouredWrappedText(result.Output, ElezenImgui.GetBooleanColour(result.Success));
        }
    }

    private void DrawGeneralSection(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        ModernSection.Header(FontAwesomeIcon.InfoCircle, "General");
        DrawReadOnlyField("##CharaDataCode", dataDto.FullId, "Chara Data Code", width: 200);
        ImGui.SameLine();
        if (ElezenImgui.IconButton(FontAwesomeIcon.Copy))
        {
            ImGui.SetClipboardText(dataDto.FullId);
        }
        ElezenImgui.AttachTooltip("Copy Code to Clipboard");

        DrawReadOnlyField("##CreationDate", dataDto.CreatedDate.ToLocalTime().ToString(CultureInfo.CurrentCulture), "Creation Date", width: 200);
        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(20);
        ImGui.SameLine();
        DrawReadOnlyField("##LastUpdate", dataDto.UpdatedDate.ToLocalTime().ToString(CultureInfo.CurrentCulture), "Last Update Date", width: 200);
        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(23);
        ImGui.SameLine();
        DrawReadOnlyField("##DlCount", dataDto.DownloadCount.ToString(CultureInfo.InvariantCulture), "Download Count", width: 50);

        var description = updateDto.Description;
        ImGui.SetNextItemWidth(735);
        if (ImGui.InputText("##Description", ref description, 200))
        {
            updateDto.Description = description;
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Description");
        ElezenImgui.DrawHelpText("Description for this Character Data." + ElezenImgui.TooltipSeparator
            + "Note: the description will be visible to anyone who can access this character data. See 'Access Restrictions' and 'Sharing' below.");

        DrawExpiryEditor(updateDto);
        ImGuiHelpers.ScaledDummy(5);
        DrawDeleteButton(dataDto);
    }

    private static void DrawReadOnlyField(string id, string value, string label, float width)
    {
        var text = value;
        using (ImRaii.Disabled())
        {
            ImGui.SetNextItemWidth(width);
            ImGui.InputText(id, ref text, 255, ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
    }

    private static void DrawExpiryEditor(CharaDataExtendedUpdateDto updateDto)
    {
        var expiryDate = updateDto.ExpiryDate;
        var isExpiring = expiryDate != DateTime.MaxValue;
        if (ImGui.Checkbox("Expires", ref isExpiring))
        {
            updateDto.SetExpiry(isExpiring);
            expiryDate = updateDto.ExpiryDate;
        }
        ElezenImgui.DrawHelpText("If expiration is enabled, the uploaded character data will be automatically deleted from the server at the specified date.");

        using var disabled = ImRaii.Disabled(!isExpiring);
        ImGui.SameLine();
        DrawDateCombo("Year", expiryDate.Year, DateTime.UtcNow.Year, DateTime.UtcNow.Year + 3,
            value => updateDto.SetExpiry(value, expiryDate.Month, expiryDate.Day));
        ImGui.SameLine();
        DrawDateCombo("Month", expiryDate.Month, 1, 12,
            value => updateDto.SetExpiry(expiryDate.Year, value, expiryDate.Day));
        ImGui.SameLine();
        DrawDateCombo("Day", expiryDate.Day, 1, DateTime.DaysInMonth(expiryDate.Year, expiryDate.Month),
            value => updateDto.SetExpiry(expiryDate.Year, expiryDate.Month, value));
    }

    private static void DrawDateCombo(string label, int selected, int first, int last, Action<int> setValue)
    {
        ImGui.SetNextItemWidth(100);
        if (!ImGui.BeginCombo(label, selected.ToString(CultureInfo.InvariantCulture)))
        {
            return;
        }

        for (var value = first; value <= last; value++)
        {
            if (ImGui.Selectable(value.ToString(CultureInfo.InvariantCulture), value == selected))
            {
                setValue(value);
            }
        }

        ImGui.EndCombo();
    }

    private void DrawDeleteButton(CharaDataFullExtendedDto dataDto)
    {
        using (ImRaii.Disabled(!ElezenImgui.CtrlPressed()))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete Character Data"))
            {
                _ = _charaDataManager.DeleteCharaData(dataDto);
                _ctx.SelectedDtoId = string.Empty;
            }
        }

        if (!ElezenImgui.CtrlPressed())
        {
            ElezenImgui.AttachTooltip("Hold CTRL and click to delete the current data. This operation is irreversible.");
        }
    }

    private void DrawAccessSection(CharaDataExtendedUpdateDto updateDto)
    {
        ModernSection.Header(FontAwesomeIcon.UserShield, "Access and Sharing");
        DrawAccessCombo(updateDto);
        DrawSpecificAccessLists(updateDto);
        DrawSharingCombo(updateDto);
        ImGuiHelpers.ScaledDummy(10);
    }

    private static void DrawAccessCombo(CharaDataExtendedUpdateDto updateDto)
    {
        var accessType = updateDto.AccessType;
        ImGui.SetNextItemWidth(LabelColumnWidth);
        if (ImGui.BeginCombo("Access Restrictions", CharaDataHubContext.GetAccessTypeString(accessType)))
        {
            foreach (var value in Enum.GetValues<AccessTypeDto>())
            {
                if (ImGui.Selectable(CharaDataHubContext.GetAccessTypeString(value), value == accessType))
                {
                    updateDto.AccessType = value;
                }
            }

            ImGui.EndCombo();
        }

        ElezenImgui.DrawHelpText("You can control who has access to your character data based on the access restrictions." + ElezenImgui.TooltipSeparator
            + "Specified: Only people and syncshells you directly specify in 'Specific Individuals / Syncshells' can access this character data" + Environment.NewLine
            + "Direct Pairs: Only people you have directly paired can access this character data" + Environment.NewLine
            + "All Pairs: All people you have paired can access this character data" + Environment.NewLine
            + "Everyone: Everyone can access this character data" + ElezenImgui.TooltipSeparator
            + "Note: To access your character data the person in question requires to have the code. Exceptions for 'Shared' data, see 'Sharing' below." + Environment.NewLine
            + "Note: For 'Direct' and 'All Pairs' the pause state plays a role. Paused people will not be able to access your character data." + Environment.NewLine
            + "Note: Directly specified Individuals or Syncshells in the 'Specific Individuals / Syncshells' list will be able to access your character data regardless of pause or pair state.");
    }

    private static void DrawSharingCombo(CharaDataExtendedUpdateDto updateDto)
    {
        var shareType = updateDto.ShareType;
        ImGui.SetNextItemWidth(LabelColumnWidth);
        using (ImRaii.Disabled(updateDto.AccessType == AccessTypeDto.Public))
        {
            if (ImGui.BeginCombo("Sharing", CharaDataHubContext.GetShareTypeString(shareType)))
            {
                foreach (var value in Enum.GetValues<ShareTypeDto>())
                {
                    if (ImGui.Selectable(CharaDataHubContext.GetShareTypeString(value), value == shareType))
                    {
                        updateDto.ShareType = value;
                    }
                }

                ImGui.EndCombo();
            }
        }

        ElezenImgui.DrawHelpText("This regulates how you want to distribute this character data." + ElezenImgui.TooltipSeparator
            + "Code Only: People require to have the code to download this character data" + Environment.NewLine
            + "Shared: People that are allowed through 'Access Restrictions' will have this character data entry displayed in 'Shared with You' (it can also be accessed through the code)" + ElezenImgui.TooltipSeparator
            + "Note: Shared is incompatible with Access Restriction 'Everyone'");
    }

    private void DrawSpecificAccessLists(CharaDataExtendedUpdateDto updateDto)
    {
        ElezenImgui.DrawTree("Access for Specific Individuals / Syncshells", () =>
        {
            using (ImRaii.PushId("user"))
            {
                DrawUserAccessList(updateDto);
            }
            ImGui.SameLine();
            ImGuiHelpers.ScaledDummy(20);
            ImGui.SameLine();
            using (ImRaii.PushId("group"))
            {
                DrawGroupAccessList(updateDto);
            }

            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(5);
        });
    }

    private void DrawUserAccessList(CharaDataExtendedUpdateDto updateDto)
    {
        using var group = ImRaii.Group();
        ElezenImgui.InputComboHybrid("##AliasToAdd", "##AliasToAddPicker", ref _specificIndividualAdd, _pairManager.DirectPairs,
            static pair => (pair.UserData.UID, pair.UserData.Alias, pair.UserData.AliasOrUID, pair.GetNoteOrName()));
        ImGui.SameLine();

        var canAdd = !string.IsNullOrEmpty(_specificIndividualAdd)
            && !updateDto.UserList.Any(user => MatchesUser(user, _specificIndividualAdd));
        using (ImRaii.Disabled(!canAdd))
        {
            if (ElezenImgui.IconButton(FontAwesomeIcon.Plus))
            {
                updateDto.AddUserToList(_specificIndividualAdd);
                _specificIndividualAdd = string.Empty;
            }
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("UID/Vanity UID to Add");
        ElezenImgui.DrawHelpText("Users added to this list will be able to access this character data regardless of your pause or pair state with them." + ElezenImgui.TooltipSeparator
            + "Note: Mistyped entries will be automatically removed on updating data to server.");

        using (ImRaii.ListBox("Allowed Individuals", new Vector2(200, 200)))
        {
            foreach (var user in updateDto.UserList.ToList())
            {
                var label = string.IsNullOrEmpty(user.Alias) ? user.UID : $"{user.Alias} ({user.UID})";
                if (ImGui.Selectable(label, string.Equals(user.UID, _selectedSpecificUserIndividual, StringComparison.Ordinal)))
                {
                    _selectedSpecificUserIndividual = user.UID;
                }
            }
        }

        using (ImRaii.Disabled(string.IsNullOrEmpty(_selectedSpecificUserIndividual)))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Remove selected User"))
            {
                updateDto.RemoveUserFromList(_selectedSpecificUserIndividual);
                _selectedSpecificUserIndividual = string.Empty;
            }
        }
    }

    private void DrawGroupAccessList(CharaDataExtendedUpdateDto updateDto)
    {
        using var groupScope = ImRaii.Group();
        ElezenImgui.InputComboHybrid("##GroupAliasToAdd", "##GroupAliasToAddPicker", ref _specificGroupAdd, _pairManager.Groups.Keys,
            group => (group.GID, group.Alias, group.AliasOrGID, _notesStore.GetNoteForGid(group.GID)));
        ImGui.SameLine();

        var canAdd = !string.IsNullOrEmpty(_specificGroupAdd)
            && !updateDto.GroupList.Any(group => MatchesGroup(group, _specificGroupAdd));
        using (ImRaii.Disabled(!canAdd))
        {
            if (ElezenImgui.IconButton(FontAwesomeIcon.Plus))
            {
                updateDto.AddGroupToList(_specificGroupAdd);
                _specificGroupAdd = string.Empty;
            }
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("GID/Vanity GID to Add");
        ElezenImgui.DrawHelpText("Users in Syncshells added to this list will be able to access this character data regardless of your pause or pair state with them." + ElezenImgui.TooltipSeparator
            + "Note: Mistyped entries will be automatically removed on updating data to server.");

        using (ImRaii.ListBox("Allowed Syncshells", new Vector2(200, 200)))
        {
            foreach (var group in updateDto.GroupList.ToList())
            {
                var label = string.IsNullOrEmpty(group.Alias) ? group.GID : $"{group.Alias} ({group.GID})";
                if (ImGui.Selectable(label, string.Equals(group.GID, _selectedSpecificGroupIndividual, StringComparison.Ordinal)))
                {
                    _selectedSpecificGroupIndividual = group.GID;
                }
            }
        }

        using (ImRaii.Disabled(string.IsNullOrEmpty(_selectedSpecificGroupIndividual)))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Remove selected Syncshell"))
            {
                updateDto.RemoveGroupFromList(_selectedSpecificGroupIndividual);
                _selectedSpecificGroupIndividual = string.Empty;
            }
        }
    }

    private static bool MatchesUser(UserData user, string value)
    {
        return string.Equals(user.UID, value, StringComparison.Ordinal)
            || string.Equals(user.Alias, value, StringComparison.Ordinal);
    }

    private static bool MatchesGroup(GroupData group, string value)
    {
        return string.Equals(group.GID, value, StringComparison.Ordinal)
            || string.Equals(group.Alias, value, StringComparison.Ordinal);
    }

    private void DrawAppearanceSection(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        ModernSection.Header(FontAwesomeIcon.Tshirt, "Appearance");
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowRight, "Set Appearance to Current Appearance"))
        {
            _charaDataManager.SetAppearanceData(dataDto.Id);
        }
        ElezenImgui.DrawHelpText("This will overwrite the appearance data currently stored in this Character Data entry with your current appearance.");

        ImGui.SameLine();
        using (ImRaii.Disabled(dataDto.HasMissingFiles || !updateDto.IsAppearanceEqual || _charaDataManager.DataApplication.IsRunning))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.CheckCircle, "Preview Saved Apperance on Self"))
            {
                _charaDataManager.ApplyDataToSelf(dataDto);
            }
        }
        ElezenImgui.DrawHelpText("This will download and apply the saved character data to yourself. Once loaded it will automatically revert itself within 15 seconds." + ElezenImgui.TooltipSeparator
            + "Note: Weapons will not be displayed correctly unless using the same job as the saved data.");

        DrawBooleanRow("Contains Glamourer Data", !string.IsNullOrEmpty(updateDto.GlamourerData));
        DrawFileSummary(dataDto, updateDto);
        DrawBooleanRow("Contains Manipulation Data", !string.IsNullOrEmpty(updateDto.ManipulationData));
        DrawBooleanRow("Contains Customize+ Data", !string.IsNullOrEmpty(updateDto.CustomizeData));
    }

    private static void DrawBooleanRow(string label, bool value)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(LabelColumnWidth);
        ElezenImgui.GetBooleanIcon(value, false);
    }

    private void DrawFileSummary(CharaDataFullExtendedDto dataDto, CharaDataExtendedUpdateDto updateDto)
    {
        var hasFiles = updateDto.FileGamePaths.Count > 0 || dataDto.OriginalFiles.Count != 0;
        ImGui.TextUnformatted("Contains Files");
        ImGui.SameLine(LabelColumnWidth);
        ElezenImgui.GetBooleanIcon(hasFiles, false);
        if (!hasFiles)
        {
            return;
        }

        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(20, 1);
        ImGui.SameLine();
        var x = ImGui.GetCursorPosX();
        ImGui.NewLine();
        ImGui.SameLine(x);

        if (!updateDto.IsAppearanceEqual)
        {
            ElezenImgui.ColouredWrappedText("New data was set. It may contain files that require to be uploaded (will happen on Saving to server)", ImGuiColors.DalamudYellow);
            return;
        }

        ImGui.TextUnformatted($"{dataDto.FileGamePaths.DistinctBy(static path => path.HashOrFileSwap).Count()} unique file hashes (original upload: {dataDto.OriginalFiles.DistinctBy(static path => path.HashOrFileSwap).Count()} file hashes)");
        ImGui.NewLine();
        ImGui.SameLine(x);
        ImGui.TextUnformatted($"{dataDto.FileGamePaths.Count} associated game paths");
        ImGui.NewLine();
        ImGui.SameLine(x);
        ImGui.TextUnformatted($"{(dataDto.FileSwaps ?? []).Count} file swaps");
        ImGui.NewLine();
        ImGui.SameLine(x);

        if (!dataDto.HasMissingFiles)
        {
            ElezenImgui.ColouredWrappedText("All files to download this character data are present on the server", ImGuiColors.HealerGreen);
            return;
        }

        ElezenImgui.ColouredWrappedText($"{dataDto.MissingFiles.DistinctBy(static path => path.HashOrFileSwap).Count()} files to download this character data are missing on the server", ImGuiColors.DalamudRed);
        ImGui.NewLine();
        ImGui.SameLine(x);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowCircleUp, "Attempt to upload missing files and restore Character Data"))
        {
            _charaDataManager.UploadMissingFiles(dataDto.Id);
        }
    }

    private void DrawPoseSection(CharaDataExtendedUpdateDto updateDto)
    {
        ModernSection.Header(FontAwesomeIcon.Running, "Poses");
        var poses = updateDto.PoseList.ToList();
        using (ImRaii.Disabled(poses.Count >= MaxPoses))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add new Pose"))
            {
                updateDto.AddPose();
                poses = updateDto.PoseList.ToList();
            }
        }
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow, poses.Count == MaxPoses))
        {
            ImGui.TextUnformatted($"{poses.Count}/{MaxPoses} poses attached");
        }
        ImGuiHelpers.ScaledDummy(5);

        DrawPosePrerequisiteNotice();
        using var indent = ImRaii.PushIndent(10f);
        for (var index = 0; index < poses.Count; index++)
        {
            DrawPoseRow(updateDto, poses[index], index + 1);
        }
    }

    private void DrawPosePrerequisiteNotice()
    {
        if (!_dalamudUtilService.IsInGpose && _charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(5);
            ElezenImgui.DrawGroupedCenteredColorText("To attach pose and world data you need to be in GPose.", ImGuiColors.DalamudYellow);
            ImGuiHelpers.ScaledDummy(5);
        }
        else if (!_charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(5);
            ElezenImgui.DrawGroupedCenteredColorText("To attach pose and world data Brio requires to be installed.", ImGuiColors.DalamudRed);
            ImGuiHelpers.ScaledDummy(5);
        }
    }

    private void DrawPoseRow(CharaDataExtendedUpdateDto updateDto, PoseEntry pose, int poseNumber)
    {
        ImGui.AlignTextToFramePadding();
        using var id = ImRaii.PushId("pose" + poseNumber);
        ImGui.TextUnformatted(poseNumber.ToString(CultureInfo.InvariantCulture));
        DrawPoseStateIcon(updateDto, pose);
        ImGui.SameLine(75);

        if (pose.Description == null && pose.WorldData == null && pose.PoseData == null)
        {
            ElezenImgui.ColouredText("Pose scheduled for deletion", ImGuiColors.DalamudYellow);
        }
        else
        {
            DrawEditablePose(updateDto, pose, poseNumber);
        }

        if (updateDto.PoseHasChanges(pose))
        {
            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Undo, "Undo"))
            {
                updateDto.RevertDeletion(pose);
            }
        }
    }

    private static void DrawPoseStateIcon(CharaDataExtendedUpdateDto updateDto, PoseEntry pose)
    {
        if (pose.Id == null)
        {
            ImGui.SameLine(50);
            ElezenImgui.ShowIcon(FontAwesomeIcon.Plus, ImGuiColors.DalamudYellow);
            ElezenImgui.AttachTooltip("This pose has not been added to the server yet. Save changes to upload this Pose data.");
            return;
        }

        if (updateDto.PoseHasChanges(pose))
        {
            ImGui.SameLine(50);
            ElezenImgui.ShowIcon(FontAwesomeIcon.ExclamationTriangle, ImGuiColors.DalamudYellow);
            ElezenImgui.AttachTooltip("This pose has changes that have not been saved to the server yet.");
        }
    }

    private void DrawEditablePose(CharaDataExtendedUpdateDto updateDto, PoseEntry pose, int poseNumber)
    {
        var description = pose.Description ?? string.Empty;
        if (ImGui.InputTextWithHint("##description", "Description", ref description, 100))
        {
            pose.Description = description;
            updateDto.UpdatePoseList();
        }
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Trash, "Delete"))
        {
            updateDto.RemovePose(pose);
        }

        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(10, 1);
        ImGui.SameLine();
        DrawPoseDataControls(updateDto, pose, poseNumber);
        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(10, 1);
        ImGui.SameLine();
        DrawWorldDataControls(updateDto, pose, poseNumber);
    }

    private void DrawPoseDataControls(CharaDataExtendedUpdateDto updateDto, PoseEntry pose, int poseNumber)
    {
        var hasPoseData = !string.IsNullOrEmpty(pose.PoseData);
        ElezenImgui.ShowIcon(FontAwesomeIcon.Running, ElezenImgui.GetBooleanColour(hasPoseData));
        ElezenImgui.AttachTooltip(hasPoseData
            ? "This Pose entry has pose data attached"
            : "This Pose entry has no pose data attached");
        ImGui.SameLine();

        using (ImRaii.Disabled(!_dalamudUtilService.IsInGpose || _charaDataManager.AttachingPose.IsRunning || !_charaDataManager.BrioAvailable))
        {
            using var setId = ImRaii.PushId("poseSet" + poseNumber);
            if (ElezenImgui.IconButton(FontAwesomeIcon.Plus))
            {
                _charaDataManager.AttachPoseData(pose, updateDto);
            }
            ElezenImgui.AttachTooltip("Apply current pose data to pose");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!hasPoseData))
        {
            using var deleteId = ImRaii.PushId("poseDelete" + poseNumber);
            if (ElezenImgui.IconButton(FontAwesomeIcon.Trash))
            {
                pose.PoseData = string.Empty;
                updateDto.UpdatePoseList();
            }
            ElezenImgui.AttachTooltip("Delete current pose data from pose");
        }
    }

    private void DrawWorldDataControls(CharaDataExtendedUpdateDto updateDto, PoseEntry pose, int poseNumber)
    {
        var worldData = pose.WorldData ?? default;
        var hasWorldData = worldData != default;
        ElezenImgui.ShowIcon(FontAwesomeIcon.Globe, ElezenImgui.GetBooleanColour(hasWorldData));
        ElezenImgui.AttachTooltip(hasWorldData
            ? "This Pose has world data attached." + ElezenImgui.TooltipSeparator + "Click to show location on map"
            : "This Pose has no world data attached.");
        if (hasWorldData && ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            PlayerInteractionService.SetMarkerAndOpenMap(
                new Vector3(worldData.PositionX, worldData.PositionY, worldData.PositionZ),
                _dalamudUtilService.Maps[worldData.LocationInfo.MapId].Map);
        }
        ImGui.SameLine();

        using (ImRaii.Disabled(!_dalamudUtilService.IsInGpose || _charaDataManager.AttachingPose.IsRunning || !_charaDataManager.BrioAvailable))
        {
            using var setId = ImRaii.PushId("worldSet" + poseNumber);
            if (ElezenImgui.IconButton(FontAwesomeIcon.Plus))
            {
                _charaDataManager.AttachWorldData(pose, updateDto);
            }
            ElezenImgui.AttachTooltip("Apply current world position data to pose");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!hasWorldData))
        {
            using var deleteId = ImRaii.PushId("worldDelete" + poseNumber);
            if (ElezenImgui.IconButton(FontAwesomeIcon.Trash))
            {
                pose.WorldData = default(WorldData);
                updateDto.UpdatePoseList();
            }
            ElezenImgui.AttachTooltip("Delete current world position data from pose");
        }
    }

    private static void SectionGap()
    {
        ImGuiHelpers.ScaledDummy(5);
    }
}
