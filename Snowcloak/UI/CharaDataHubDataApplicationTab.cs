using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using System.Globalization;
using System.Threading;

namespace Snowcloak.UI;

internal sealed class CharaDataHubDataApplicationTab
{
    private static readonly string[] ApplyDataTabs = ["Favorites", "Code", "Your Own", "Shared With You", "From MCDF"];

    private readonly CharaDataHubContext _ctx;
    private readonly CharaDataManager _charaDataManager;
    private readonly CharaDataConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly CharaDataStateConfigService _stateConfigService;
    private readonly NotesStore _notesStore;
    private readonly FileDialogManager _fileDialogManager;
    private readonly CancellationToken _disposalToken;

    private readonly Dictionary<string, FavoriteEntry> _filteredFavorites = [];
    private Dictionary<string, List<CharaDataMetaInfoExtendedDto>> _filteredShared = [];
    private string _applyDataTab = "Favorites";
    private string _favoriteOwnerFilter = string.Empty;
    private string _favoriteDescriptionFilter = string.Empty;
    private bool _favoritePoseOnly;
    private bool _favoriteWorldOnly;
    private string _sharedOwnerFilter = string.Empty;
    private string _sharedDescriptionFilter = string.Empty;
    private bool _sharedDownloadableOnly;
    private string _importCode = string.Empty;

    public CharaDataHubDataApplicationTab(
        CharaDataHubContext ctx,
        CharaDataManager charaDataManager,
        CharaDataConfigService configService,
        CharaDataStateConfigService stateConfigService,
        NotesStore notesStore,
        DalamudUtilService dalamudUtilService,
        FileDialogManager fileDialogManager,
        CancellationToken disposalToken)
    {
        _ctx = ctx;
        _charaDataManager = charaDataManager;
        _configService = configService;
        _stateConfigService = stateConfigService;
        _notesStore = notesStore;
        _dalamudUtilService = dalamudUtilService;
        _fileDialogManager = fileDialogManager;
        _disposalToken = disposalToken;
    }

    public void OpenSharedWithOwner(string ownerFilter)
    {
        _sharedOwnerFilter = ownerFilter;
        _ctx.OpenDataApplicationShared = true;
        UpdateSharedFilter();
    }

    public void ResetTransientState()
    {
        _sharedOwnerFilter = string.Empty;
        _sharedDescriptionFilter = string.Empty;
        _sharedDownloadableOnly = false;
        _filteredShared = [];
        _importCode = string.Empty;
    }

    public void RefreshFavoriteFilter()
    {
        _filteredFavorites.Clear();
        foreach (var favorite in _stateConfigService.Current.FavoriteCodes)
        {
            var uid = favorite.Key.Split(':')[0];
            var note = _notesStore.GetNoteForUid(uid) ?? string.Empty;
            var hasMetaInfo = _charaDataManager.TryGetMetaInfo(favorite.Key, out var metaInfo);
            if (!FavoritePassesFilter(favorite.Value, uid, note, metaInfo))
            {
                continue;
            }

            _filteredFavorites[favorite.Key] = new FavoriteEntry(favorite.Value, metaInfo, hasMetaInfo);
        }
    }

    public void Draw()
    {
        ModernSection.Header(FontAwesomeIcon.Magic, "Apply Character Appearance");
        ImGuiHelpers.ScaledDummy(5);
        DrawTargetStatus();
        ImGuiHelpers.ScaledDummy(10);

        if (_ctx.OpenDataApplicationShared)
        {
            _applyDataTab = "Shared With You";
        }

        _applyDataTab = ModernTabBar.Draw("ApplyDataTabs", ApplyDataTabs, _applyDataTab);
        ImGuiHelpers.ScaledDummy(3);

        switch (_applyDataTab)
        {
            case "Favorites":
                DrawFavoritesTab();
                break;
            case "Code":
                DrawCodeTab();
                break;
            case "Your Own":
                DrawOwnDataTab();
                break;
            case "Shared With You":
                DrawSharedTab();
                break;
            case "From MCDF":
                DrawMcdfTab();
                break;
        }
    }

    private void DrawTargetStatus()
    {
        if (_dalamudUtilService.IsInGpose)
        {
            ImGui.TextUnformatted("GPose Target");
            ImGui.SameLine(200);
            ElezenImgui.ColouredText(_ctx.CharaName(_ctx.GposeTarget), ElezenImgui.GetBooleanColour(_ctx.HasValidGposeTarget));
        }

        if (!_ctx.HasValidGposeTarget)
        {
            ImGuiHelpers.ScaledDummy(3);
            CharaDataHubCard.Warning("Applying data is only available in GPose with a valid selected GPose target.");
        }
    }

    private void DrawFavoritesTab()
    {
        using var id = ImRaii.PushId("byFavorite");
        if (_filteredFavorites.Count == 0 && _stateConfigService.Current.FavoriteCodes.Count > 0)
        {
            RefreshFavoriteFilter();
        }

        ImGuiHelpers.ScaledDummy(5);
        DrawFavoriteFilters();
        ImGuiHelpers.ScaledDummy(5);
        ImGui.Separator();

        using var child = ImRaii.Child("favorite");
        ImGuiHelpers.ScaledDummy(5);
        using var indent = ImRaii.PushIndent(5f);
        foreach (var favorite in _filteredFavorites.OrderByDescending(static item => item.Value.Favorite.LastDownloaded))
        {
            DrawFavoriteCard(favorite.Key, favorite.Value);
            ImGuiHelpers.ScaledDummy(5);
        }

        if (_stateConfigService.Current.FavoriteCodes.Count == 0)
        {
            CharaDataHubCard.Info("You have no favorites yet. Add favorites from the other tabs to use this one.");
        }
    }

    private void DrawFavoriteFilters()
    {
        ElezenImgui.DrawTree(FormatFilterLabel("Filters", CountFavoriteFilters()), () =>
        {
            var width = ImGui.GetWindowContentRegionMax().X - ImGui.GetCursorPosX();
            ImGui.SetNextItemWidth(width);
            if (ImGui.InputTextWithHint("##ownFilter", "Code/Owner Filter", ref _favoriteOwnerFilter, 100))
            {
                RefreshFavoriteFilter();
            }

            ImGui.SetNextItemWidth(width);
            if (ImGui.InputTextWithHint("##descFilter", "Custom Description Filter", ref _favoriteDescriptionFilter, 100))
            {
                RefreshFavoriteFilter();
            }

            if (ImGui.Checkbox("Only show entries with pose data", ref _favoritePoseOnly))
            {
                RefreshFavoriteFilter();
            }
            if (ImGui.Checkbox("Only show entries with world data", ref _favoriteWorldOnly))
            {
                RefreshFavoriteFilter();
            }
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Ban, "Reset Filter"))
            {
                _favoriteOwnerFilter = string.Empty;
                _favoriteDescriptionFilter = string.Empty;
                _favoritePoseOnly = false;
                _favoriteWorldOnly = false;
                RefreshFavoriteFilter();
            }
        });
    }

    private bool FavoritePassesFilter(CharaDataFavorite favorite, string uid, string note, CharaDataMetaInfoExtendedDto? metaInfo)
    {
        return (string.IsNullOrEmpty(_favoriteOwnerFilter)
                || note.Contains(_favoriteOwnerFilter, StringComparison.OrdinalIgnoreCase)
                || uid.Contains(_favoriteOwnerFilter, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrEmpty(_favoriteDescriptionFilter)
                || favorite.CustomDescription.Contains(_favoriteDescriptionFilter, StringComparison.OrdinalIgnoreCase)
                || (metaInfo != null && metaInfo.Description.Contains(_favoriteDescriptionFilter, StringComparison.OrdinalIgnoreCase)))
            && (!_favoritePoseOnly || metaInfo?.HasPoses == true)
            && (!_favoriteWorldOnly || metaInfo?.HasWorldData == true);
    }

    private void DrawFavoriteCard(string code, FavoriteEntry favorite)
    {
        var cursor = ImGui.GetCursorPos();
        var max = ImGui.GetWindowContentRegionMax();
        ElezenImgui.DrawGrouped(() =>
        {
            using var id = ImRaii.PushId(code);
            ImGui.AlignTextToFramePadding();
            _ctx.DrawFavorite(code);
            ImGui.SameLine();
            var indentStart = ImGui.GetCursorPosX();
            var width = max.X - cursor.X;

            DrawFavoriteHeader(code, favorite, width);
            using var indent = ImRaii.PushIndent(indentStart - cursor.X);
            DrawFavoriteOwnerLine(code, favorite.MetaInfo);
            ImGui.TextUnformatted("Last Use: ");
            ImGui.SameLine();
            ImGui.TextUnformatted(favorite.Favorite.LastDownloaded == DateTime.MaxValue
                ? "Never"
                : favorite.Favorite.LastDownloaded.ToString(CultureInfo.CurrentCulture));

            var description = favorite.Favorite.CustomDescription;
            ImGui.SetNextItemWidth(width - indentStart);
            if (ImGui.InputTextWithHint("##desc", "Custom Description for Favorite", ref description, 100))
            {
                _stateConfigService.Update(_ => favorite.Favorite.CustomDescription = description);
                RefreshFavoriteFilter();
            }

            _ctx.DrawPoseData(favorite.MetaInfo, _ctx.GposeTarget, _ctx.HasValidGposeTarget);
        });
    }

    private void DrawFavoriteHeader(string code, FavoriteEntry favorite, float width)
    {
        var metaInfo = favorite.MetaInfo;
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey, !favorite.DownloadedMetaInfo))
        using (ImRaii.PushColor(ImGuiCol.Text, ElezenImgui.GetBooleanColour(metaInfo != null), favorite.DownloadedMetaInfo))
        {
            ImGui.TextUnformatted(code);
        }

        var offset = width
            - ElezenImgui.GetIconSize(FontAwesomeIcon.Check).X
            - ElezenImgui.GetIconButtonSize(FontAwesomeIcon.ArrowsSpin).X
            - ElezenImgui.GetIconButtonSize(FontAwesomeIcon.ArrowRight).X
            - ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Plus).X
            - ImGui.GetStyle().ItemSpacing.X * 3.5f;

        ImGui.SameLine();
        ImGui.SetCursorPosX(offset);
        DrawMetaInfoPresence(favorite);
        ImGui.SameLine();
        DrawMetaRefreshButton(code);
        ImGui.SameLine();
        DrawApplyButton(metaInfo, "Apply Character Data to GPose Target");
        ImGui.SameLine();
        DrawSpawnButton(metaInfo, "Spawn Actor with Brio and apply Character Data");
    }

    private static void DrawMetaInfoPresence(FavoriteEntry favorite)
    {
        if (!favorite.DownloadedMetaInfo)
        {
            ElezenImgui.ShowIcon(FontAwesomeIcon.QuestionCircle, ImGuiColors.DalamudGrey);
            ElezenImgui.AttachTooltip("Unknown accessibility state. Click the button on the right to refresh.");
            return;
        }

        ElezenImgui.GetBooleanIcon(favorite.MetaInfo != null, false);
        if (favorite.MetaInfo != null)
        {
            ElezenImgui.AttachTooltip("Metainfo present" + ElezenImgui.TooltipSeparator
                    + $"Last Updated: {favorite.MetaInfo.UpdatedDate.ToString(CultureInfo.CurrentCulture)}" + Environment.NewLine
                + $"Description: {favorite.MetaInfo.Description}" + Environment.NewLine
                + $"Poses: {favorite.MetaInfo.PoseData.Count}");
        }
        else
        {
            ElezenImgui.AttachTooltip("Metainfo could not be downloaded." + ElezenImgui.TooltipSeparator
                + "The data associated with the code is either not present on the server anymore or you have no access to it");
        }
    }

    private void DrawMetaRefreshButton(string code)
    {
        var isInTimeout = _charaDataManager.IsInTimeout(code);
        using (ImRaii.Disabled(isInTimeout))
        {
            if (ElezenImgui.IconButton(FontAwesomeIcon.ArrowsSpin))
            {
                _charaDataManager.DownloadMetaInfo(code, false);
                RefreshFavoriteFilter();
            }
        }
        ElezenImgui.AttachTooltip(isInTimeout
            ? "Timeout for refreshing active, please wait before refreshing again."
            : "Refresh data for this entry from the Server.");
    }

    private void DrawFavoriteOwnerLine(string code, CharaDataMetaInfoExtendedDto? metaInfo)
    {
        var uid = code.Split(':')[0];
        var display = metaInfo?.Uploader.AliasOrUID ?? uid;
        var note = _notesStore.GetNoteForUid(uid);
        ImGui.TextUnformatted(note == null ? display : $"{note} ({display})");
    }

    private void DrawCodeTab()
    {
        using var id = ImRaii.PushId("byCodeTab");
        using var child = ImRaii.Child("sharedWithYouByCode", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
        CharaDataHubWidgets.DrawHelpFoldout(_configService, "You can apply character data you have a code for in this tab. Provide the code in it's given format \"OwnerUID:DataId\" into the field below and click on "
            + "\"Get Info from Code\". This will provide you basic information about the data behind the code. Afterwards select an actor in GPose and press on \"Download and apply to <actor>\"." + Environment.NewLine + Environment.NewLine
            + "Description: as set by the owner of the code to give you more or additional information of what this code may contain." + Environment.NewLine
            + "Last Update: the date and time the owner of the code has last updated the data." + Environment.NewLine
            + "Is Downloadable: whether or not the code is downloadable and applicable. If the code is not downloadable, contact the owner so they can attempt to fix it." + Environment.NewLine + Environment.NewLine
            + "To download a code the code requires correct access permissions to be set by the owner. If getting info from the code fails, contact the owner to make sure they set their Access Permissions for the code correctly.");

        ImGuiHelpers.ScaledDummy(5);
        ImGui.InputTextWithHint("##importCode", "Enter Data Code", ref _importCode, 100);
        using (ImRaii.Disabled(string.IsNullOrEmpty(_importCode)))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowCircleDown, "Get Info from Code"))
            {
                _charaDataManager.DownloadMetaInfo(_importCode);
            }
        }

        DrawApplyButton(_charaDataManager.LastDownloadedMetaInfo, "Apply this Character Data to the current GPose actor", large: true);
        ImGui.SameLine();
        DrawSpawnButton(_charaDataManager.LastDownloadedMetaInfo, "Spawn a new Brio actor and apply this Character Data", large: true);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        _ctx.DrawAddOrRemoveFavorite(_charaDataManager.LastDownloadedMetaInfo);

        DrawMetaInfoDownloadStatus();
        DrawDownloadedMetaInfoSummary(_charaDataManager.LastDownloadedMetaInfo);
    }

    private void DrawMetaInfoDownloadStatus()
    {
        ImGui.NewLine();
        if (_charaDataManager.MetaInfoDownload.IsRunning)
        {
            ElezenImgui.ColouredWrappedText("Downloading meta info. Please wait.", ImGuiColors.DalamudYellow);
        }
        if (_charaDataManager.MetaInfoDownload.IsCompleted && !_charaDataManager.MetaInfoDownload.Result.Success)
        {
            ElezenImgui.ColouredWrappedText(_charaDataManager.MetaInfoDownload.Result.Result, ImGuiColors.DalamudRed);
        }
    }

    private void DrawDownloadedMetaInfoSummary(CharaDataMetaInfoExtendedDto? metaInfo)
    {
        using var disabled = ImRaii.Disabled(metaInfo == null);
        ImGuiHelpers.ScaledDummy(5);
        DrawSummaryRow("Description", string.IsNullOrEmpty(metaInfo?.Description) ? "-" : metaInfo.Description);
        DrawSummaryRow("Last Update", metaInfo?.UpdatedDate.ToLocalTime().ToString(CultureInfo.CurrentCulture) ?? "-");
        ImGui.TextUnformatted("Is Downloadable");
        ImGui.SameLine(150);
        ElezenImgui.GetBooleanIcon(metaInfo?.CanBeDownloaded ?? false, inline: false);
        ImGui.TextUnformatted("Poses");
        ImGui.SameLine(150);
        if (metaInfo?.HasPoses == true)
        {
            _ctx.DrawPoseData(metaInfo, _ctx.GposeTarget, _ctx.HasValidGposeTarget);
        }
        else
        {
            ElezenImgui.GetBooleanIcon(false, false);
        }
    }

    private static void DrawSummaryRow(string label, string value)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(150);
        ElezenImgui.WrappedText(value);
    }

    private void DrawOwnDataTab()
    {
        using var id = ImRaii.PushId("yourOwnTab");
        CharaDataHubWidgets.DrawHelpFoldout(_configService, "You can apply character data you created yourself in this tab. If the list is not populated press on \"Download your Character Data\"." + Environment.NewLine + Environment.NewLine
            + "To create new and edit your existing character data use the \"MCD Online\" tab.");

        ImGuiHelpers.ScaledDummy(5);
        DrawOwnDataRefreshButton();
        ImGuiHelpers.ScaledDummy(5);
        ImGui.Separator();

        using var child = ImRaii.Child("ownDataChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
        using var indent = ImRaii.PushIndent(10f);
        foreach (var data in _charaDataManager.OwnCharaData.Values)
        {
            if (_charaDataManager.TryGetMetaInfo(data.FullId, out var metaInfo))
            {
                DrawMetaInfoCard(metaInfo!, canOpen: true);
            }
        }
        ImGuiHelpers.ScaledDummy(5);
    }

    private void DrawOwnDataRefreshButton()
    {
        using (ImRaii.Disabled(_charaDataManager.OwnDataDownloading || _charaDataManager.OwnDataOnCooldown))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowCircleDown, "Download your Character Data"))
            {
                _ = _charaDataManager.GetAllData(_disposalToken);
            }
        }
        if (_charaDataManager.OwnDataOnCooldown)
        {
            ElezenImgui.AttachTooltip("You can only refresh all character data from server every minute. Please wait.");
        }
    }

    private void DrawSharedTab()
    {
        using var id = ImRaii.PushId("sharedWithYouTab");
        CharaDataHubWidgets.DrawHelpFoldout(_configService, "You can apply character data shared with you implicitly in this tab. Shared Character Data are Character Data entries that have \"Sharing\" set to \"Shared\" and you have access through those by meeting the access restrictions, "
            + "i.e. you were specified by your UID to gain access or are paired with the other user according to the Access Restrictions setting." + Environment.NewLine + Environment.NewLine
            + "Filter if needed to find a specific entry, then just press on \"Apply to <actor>\" and it will download and apply the Character Data to the currently targeted GPose actor." + Environment.NewLine + Environment.NewLine
            + "Note: Shared Data of Pairs you have paused will not be shown here.");

        ImGuiHelpers.ScaledDummy(5);
        DrawUpdateSharedDataButton();
        DrawSharedFilters();
        if (_filteredShared.Count == 0 && _charaDataManager.SharedWithYouData.Count > 0 && !_charaDataManager.SharedDataDownloading)
        {
            UpdateSharedFilter();
        }
        ImGuiHelpers.ScaledDummy(5);
        ImGui.Separator();

        using var child = ImRaii.Child("sharedWithYouChild", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
        ImGuiHelpers.ScaledDummy(5);
        foreach (var entry in _filteredShared)
        {
            var open = entry.Key.Contains(_sharedOwnerFilter, StringComparison.OrdinalIgnoreCase) && _ctx.OpenDataApplicationShared;
            if (open)
            {
                ImGui.SetNextItemOpen(true);
            }

            ElezenImgui.DrawTree($"{entry.Key} - [{entry.Value.Count} Character Data Sets]##{entry.Key}", () =>
            {
                foreach (var data in entry.Value)
                {
                    DrawMetaInfoCard(data);
                }
                ImGuiHelpers.ScaledDummy(5);
            });

            if (open)
            {
                _ctx.OpenDataApplicationShared = false;
            }
        }
    }

    private void DrawUpdateSharedDataButton()
    {
        using (ImRaii.Disabled(_charaDataManager.OwnDataDownloading || _charaDataManager.SharedDataOnCooldown))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowCircleDown, "Update Data Shared With You"))
            {
                _ = _charaDataManager.GetAllSharedData(_disposalToken).ContinueWith(_ => UpdateSharedFilter(),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
        if (_charaDataManager.SharedDataOnCooldown)
        {
            ElezenImgui.AttachTooltip("You can only refresh all character data from server every minute. Please wait.");
        }
    }

    private void DrawSharedFilters()
    {
        ElezenImgui.DrawTree($"{FormatFilterLabel("Filters", CountSharedFilters())}##filters", () =>
        {
            var width = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;
            ImGui.SetNextItemWidth(width);
            if (ImGui.InputTextWithHint("##filter", "Filter by UID/Note", ref _sharedOwnerFilter, 30))
            {
                UpdateSharedFilter();
            }
            ImGui.SetNextItemWidth(width);
            if (ImGui.InputTextWithHint("##filterDesc", "Filter by Description", ref _sharedDescriptionFilter, 50))
            {
                UpdateSharedFilter();
            }
            if (ImGui.Checkbox("Only show downloadable", ref _sharedDownloadableOnly))
            {
                UpdateSharedFilter();
            }
        });
    }

    private void UpdateSharedFilter()
    {
        if (_charaDataManager.SharedDataDownloading)
        {
            return;
        }

        _filteredShared = _charaDataManager.SharedWithYouData
            .SelectMany(static entry => entry.Value)
            .Where(PassesSharedFilter)
            .GroupBy(static entry => entry.Uploader)
            .ToDictionary(FormatSharedOwner, static group => group.ToList(), StringComparer.OrdinalIgnoreCase)
            .Where(entry => string.IsNullOrEmpty(_sharedOwnerFilter)
                || entry.Key.Contains(_sharedOwnerFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private bool PassesSharedFilter(CharaDataMetaInfoExtendedDto metaInfo)
    {
        return (!_sharedDownloadableOnly || metaInfo.CanBeDownloaded)
            && (string.IsNullOrEmpty(_sharedDescriptionFilter)
                || metaInfo.Description.Contains(_sharedDescriptionFilter, StringComparison.OrdinalIgnoreCase));
    }

    private string FormatSharedOwner(IGrouping<UserData, CharaDataMetaInfoExtendedDto> group)
    {
        var note = _notesStore.GetNoteForUid(group.Key.UID);
        return note == null ? group.Key.AliasOrUID : $"{note} ({group.Key.AliasOrUID})";
    }

    private void DrawMcdfTab()
    {
        using var id = ImRaii.PushId("applyMcdfTab");
        using var child = ImRaii.Child("applyMcdf", new(0, 0), false, ImGuiWindowFlags.AlwaysAutoResize);
        CharaDataHubWidgets.DrawHelpFoldout(_configService, "You can apply character data shared with you using a MCDF file in this tab." + Environment.NewLine + Environment.NewLine
            + "Load the MCDF first via the \"Load MCDF\" button which will give you the basic description that the owner has set during export." + Environment.NewLine
            + "You can then apply it to any handled GPose actor." + Environment.NewLine + Environment.NewLine
            + "MCDF to share with others can be generated using the \"MCDF Export\" tab at the top.");

        ImGuiHelpers.ScaledDummy(5);
        if (_charaDataManager.LoadedMcdfHeader is { IsCompleted: false })
        {
            ElezenImgui.ColouredWrappedText("Loading Character...", ImGuiColors.DalamudYellow);
            return;
        }

        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FolderOpen, "Load MCDF"))
        {
            OpenMcdfDialog();
        }
        ElezenImgui.AttachTooltip("Load MCDF Metadata into memory");

        if (_charaDataManager.LoadedMcdfHeader?.IsFaulted == true || _charaDataManager.McdfApplication.Faulted)
        {
            DrawMcdfFailure();
            return;
        }
        if (_charaDataManager.LoadedMcdfHeader?.IsCompleted == true)
        {
            DrawLoadedMcdf();
        }
    }

    private void OpenMcdfDialog()
    {
        _fileDialogManager.OpenFileDialog("Pick MCDF file", ".mcdf", (success, paths) =>
        {
            if (!success || paths.FirstOrDefault() is not string path)
            {
                return;
            }

            _configService.Update(config => config.LastSavedCharaDataLocation = Path.GetDirectoryName(path) ?? string.Empty);
            _charaDataManager.LoadMcdf(path);
        }, 1, Directory.Exists(_configService.Current.LastSavedCharaDataLocation) ? _configService.Current.LastSavedCharaDataLocation : null);
    }

    private void DrawLoadedMcdf()
    {
        var result = _charaDataManager.LoadedMcdfHeader!.Result;
        DrawSummaryRow("Loaded file", result.LoadedFile.FilePath);
        DrawSummaryRow("Description", result.LoadedFile.CharaFileData.Description);
        ImGuiHelpers.ScaledDummy(5);

        using (ImRaii.Disabled(!_ctx.HasValidGposeTarget))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowRight, "Apply"))
            {
                _ = _charaDataManager.McdfApplyToGposeTarget();
            }
            ElezenImgui.AttachTooltip($"Apply to {_ctx.GposeTarget}");
            ImGui.SameLine();
            using (ImRaii.Disabled(!_charaDataManager.BrioAvailable))
            {
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Spawn Actor and Apply"))
                {
                    _charaDataManager.McdfSpawnApplyToGposeTarget();
                }
            }
        }
    }

    private static void DrawMcdfFailure()
    {
        CharaDataHubCard.Error("Failed to read the MCDF file — it may be corrupt. Re-export it and try again.");
        CharaDataHubCard.Info("If this is your MCDF, try redrawing yourself, wait a moment, then re-export. If someone sent it to you, ask them to do the same.");
    }

    private void DrawMetaInfoCard(CharaDataMetaInfoExtendedDto data, bool canOpen = false)
    {
        ImGuiHelpers.ScaledDummy(5);
        using var entryId = ImRaii.PushId(data.FullId);
        var startX = ImGui.GetCursorPosX();
        var width = ImGui.GetWindowContentRegionMax().X - startX;

        ElezenImgui.DrawGrouped(() =>
        {
            ImGui.AlignTextToFramePadding();
            _ctx.DrawAddOrRemoveFavorite(data);
            ImGui.SameLine();
            var indentX = ImGui.GetCursorPosX();
            DrawMetaInfoHeader(data, width);
            using var indent = ImRaii.PushIndent(indentX - startX);
            DrawOpenEditorButton(data, canOpen);
            DrawMetaDescription(data, width);
            _ctx.DrawPoseData(data, _ctx.GposeTarget, _ctx.HasValidGposeTarget);
        });
    }

    private void DrawMetaInfoHeader(CharaDataMetaInfoExtendedDto data, float width)
    {
        ImGui.AlignTextToFramePadding();
        ElezenImgui.ColouredText(data.FullId, ElezenImgui.GetBooleanColour(data.CanBeDownloaded));
        if (!data.CanBeDownloaded)
        {
            ElezenImgui.AttachTooltip("This data is incomplete on the server and cannot be downloaded. Contact the owner so they can fix it. If you are the owner, review the data in the MCD Online tab.");
        }

        var offset = width
            - ElezenImgui.GetIconSize(FontAwesomeIcon.Calendar).X
            - ElezenImgui.GetIconButtonSize(FontAwesomeIcon.ArrowRight).X
            - ElezenImgui.GetIconButtonSize(FontAwesomeIcon.Plus).X
            - ImGui.GetStyle().ItemSpacing.X * 2;

        ImGui.SameLine();
        ImGui.SetCursorPosX(offset);
        ElezenImgui.ShowIcon(FontAwesomeIcon.Calendar);
        ElezenImgui.AttachTooltip($"Last Update: {data.UpdatedDate.ToString(CultureInfo.CurrentCulture)}");
        ImGui.SameLine();
        DrawApplyButton(data, $"Apply Character data to {_ctx.CharaName(_ctx.GposeTarget)}");
        ImGui.SameLine();
        DrawSpawnButton(data, "Spawn and Apply Character data");
    }

    private void DrawOpenEditorButton(CharaDataMetaInfoExtendedDto data, bool canOpen)
    {
        if (!canOpen)
        {
            return;
        }

        using (ImRaii.Disabled(_ctx.IsHandlingSelf))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Edit, "Open in MCD Online Editor"))
            {
                _ctx.SelectedDtoId = data.Id;
                _ctx.OpenMcdOnlineOnNextRun = true;
            }
        }
        if (_ctx.IsHandlingSelf)
        {
            ElezenImgui.AttachTooltip("Cannot use MCD Online while having Character Data applied to self.");
        }
    }

    private static void DrawMetaDescription(CharaDataMetaInfoExtendedDto data, float width)
    {
        if (string.IsNullOrEmpty(data.Description))
        {
            ElezenImgui.ColouredWrappedText("No description set", ImGuiColors.DalamudGrey, width);
        }
        else
        {
            ElezenImgui.WrappedText(data.Description, width);
        }
    }

    private void DrawApplyButton(CharaDataMetaInfoExtendedDto? metaInfo, string tooltip, bool large = false)
    {
        _ctx.GposeMetaInfoAction(meta =>
        {
            var clicked = large
                ? ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowRight, "Download and Apply")
                : ElezenImgui.IconButton(FontAwesomeIcon.ArrowRight);
            if (clicked)
            {
                _ = _charaDataManager.ApplyCharaDataToGposeTarget(meta!);
            }
        }, tooltip, metaInfo, _ctx.HasValidGposeTarget, false);
    }

    private void DrawSpawnButton(CharaDataMetaInfoExtendedDto? metaInfo, string tooltip, bool large = false)
    {
        _ctx.GposeMetaInfoAction(meta =>
        {
            var clicked = large
                ? ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Download and Spawn")
                : ElezenImgui.IconButton(FontAwesomeIcon.Plus);
            if (clicked)
            {
                _ = _charaDataManager.SpawnAndApplyData(meta!);
            }
        }, tooltip, metaInfo, _ctx.HasValidGposeTarget, true);
    }

    private int CountFavoriteFilters()
    {
        var count = 0;
        if (!string.IsNullOrEmpty(_favoriteOwnerFilter)) count++;
        if (!string.IsNullOrEmpty(_favoriteDescriptionFilter)) count++;
        if (_favoritePoseOnly) count++;
        if (_favoriteWorldOnly) count++;
        return count;
    }

    private int CountSharedFilters()
    {
        var count = 0;
        if (!string.IsNullOrEmpty(_sharedOwnerFilter)) count++;
        if (!string.IsNullOrEmpty(_sharedDescriptionFilter)) count++;
        if (_sharedDownloadableOnly) count++;
        return count;
    }

    private static string FormatFilterLabel(string label, int activeFilters)
    {
        return activeFilters == 0 ? label : $"{label} ({activeFilters} active)";
    }

    private sealed record FavoriteEntry(
        CharaDataFavorite Favorite,
        CharaDataMetaInfoExtendedDto? MetaInfo,
        bool DownloadedMetaInfo);
}
