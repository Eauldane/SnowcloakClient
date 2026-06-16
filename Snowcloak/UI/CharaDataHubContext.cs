using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Dto.CharaData;
using Snowcloak.Configuration;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Services.ServerConfiguration;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Snowcloak.UI;

internal sealed class CharaDataHubContext
{
    private readonly CharaDataManager _charaDataManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly CharaDataStateConfigService _stateConfigService;
    private readonly Lock _gposeStateLock = new();
    private bool _hasValidGposeTarget;
    private string _gposeTarget = string.Empty;
    private nint _gposeTargetAddress = nint.Zero;
    private string _selectedDtoId = string.Empty;

    public CharaDataHubContext(CharaDataManager charaDataManager, CharaDataStateConfigService stateConfigService,
        DalamudUtilService dalamudUtilService)
    {
        _charaDataManager = charaDataManager;
        _stateConfigService = stateConfigService;
        _dalamudUtilService = dalamudUtilService;
    }

    public bool AbbreviateCharaName { get; set; }
    public bool DisableUI { get; set; }
    public CancellationToken ClosalToken { get; set; }

    public void DisableDisabled(Action drawAction)
    {
        if (DisableUI) ImGui.EndDisabled();
        drawAction();
        if (DisableUI) ImGui.BeginDisabled();
    }

    public bool OpenDataApplicationShared { get; set; }
    public bool OpenMcdOnlineOnNextRun { get; set; }
    public bool IsHandlingSelf { get; set; }

    public string SelectedDtoId
    {
        get => _selectedDtoId;
        set
        {
            if (!string.Equals(_selectedDtoId, value, StringComparison.Ordinal))
            {
                _charaDataManager.Upload.Reset();
                _selectedDtoId = value;
            }
        }
    }

    public PoseEntryExtended? NearbyHovered { get; set; }

    public void SetGposeState(bool hasValidTarget, string target, nint address)
    {
        lock (_gposeStateLock)
        {
            _hasValidGposeTarget = hasValidTarget;
            _gposeTarget = target;
            _gposeTargetAddress = address;
        }
    }

    public bool HasValidGposeTarget { get { lock (_gposeStateLock) return _hasValidGposeTarget; } }
    public string GposeTarget { get { lock (_gposeStateLock) return _gposeTarget; } }
    public nint GposeTargetAddress { get { lock (_gposeStateLock) return _gposeTargetAddress; } }

    public string CharaName(string name)
    {
        if (AbbreviateCharaName)
        {
            var split = name.Split(" ");
            return split[0].First() + ". " + split[1].First() + ".";
        }

        return name;
    }

    public static string GetAccessTypeString(AccessTypeDto dto) => dto switch
    {
        AccessTypeDto.AllPairs => "All Pairs",
        AccessTypeDto.ClosePairs => "Direct Pairs",
        AccessTypeDto.Individuals => "Specified",
        AccessTypeDto.Public => "Everyone",
        _ => ((int)dto).ToString(CultureInfo.InvariantCulture)
    };

    public static string GetShareTypeString(ShareTypeDto dto) => dto switch
    {
        ShareTypeDto.Private => "Code Only",
        ShareTypeDto.Shared => "Shared",
        _ => ((int)dto).ToString(CultureInfo.InvariantCulture)
    };

    public static string GetWorldDataTooltipText(PoseEntryExtended poseEntry)
    {
        if (!poseEntry.HasWorldData) return "This Pose has no world data attached.";
        return poseEntry.WorldDataDescriptor;
    }

    public void DrawAddOrRemoveFavorite(CharaDataFullDto dto) => DrawFavorite(dto.Uploader.UID + ":" + dto.Id);

    public void DrawAddOrRemoveFavorite(CharaDataMetaInfoExtendedDto? dto)
    {
        if (dto == null) return;
        DrawFavorite(dto.FullId);
    }

    public void DrawFavorite(string id)
    {
        bool isFavorite = _stateConfigService.Current.FavoriteCodes.TryGetValue(id, out var favorite);
        if (_stateConfigService.Current.FavoriteCodes.ContainsKey(id))
        {
            ElezenImgui.ShowIcon(FontAwesomeIcon.Star, ImGuiColors.ParsedGold);
            ElezenImgui.AttachTooltip($"Custom Description: {favorite?.CustomDescription ?? string.Empty}" + ElezenImgui.TooltipSeparator
                + "Click to remove from Favorites");
        }
        else
        {
            ElezenImgui.ShowIcon(FontAwesomeIcon.Star, ImGuiColors.DalamudGrey);
            ElezenImgui.AttachTooltip("Click to add to Favorites");
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _stateConfigService.Update(c =>
            {
                if (isFavorite) c.FavoriteCodes.Remove(id);
                else c.FavoriteCodes[id] = new();
            });
        }
    }

    public void GposeMetaInfoAction(Action<CharaDataMetaInfoExtendedDto?> gposeActionDraw, string actionDescription, CharaDataMetaInfoExtendedDto? dto, bool hasValidGposeTarget, bool isSpawning)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine(actionDescription);
        bool isDisabled = false;

        void AddErrorStart(StringBuilder sb)
        {
            sb.Append(ElezenImgui.TooltipSeparator);
            sb.AppendLine("Cannot execute:");
        }

        if (dto == null)
        {
            if (!isDisabled) AddErrorStart(sb);
            sb.AppendLine("- No metainfo present");
            isDisabled = true;
        }
        if (!dto?.CanBeDownloaded ?? false)
        {
            if (!isDisabled) AddErrorStart(sb);
            sb.AppendLine("- Character is not downloadable");
            isDisabled = true;
        }
        if (!_dalamudUtilService.IsInGpose)
        {
            if (!isDisabled) AddErrorStart(sb);
            sb.AppendLine("- Requires to be in GPose");
            isDisabled = true;
        }
        if (!hasValidGposeTarget && !isSpawning)
        {
            if (!isDisabled) AddErrorStart(sb);
            sb.AppendLine("- Requires a valid GPose target");
            isDisabled = true;
        }
        if (isSpawning && !_charaDataManager.BrioAvailable)
        {
            if (!isDisabled) AddErrorStart(sb);
            sb.AppendLine("- Requires Brio to be installed.");
            isDisabled = true;
        }

        using (ImRaii.Group())
        {
            using var dis = ImRaii.Disabled(isDisabled);
            gposeActionDraw.Invoke(dto);
        }
        if (sb.Length > 0)
        {
            ElezenImgui.AttachTooltip(sb.ToString());
        }
    }

    public void GposePoseAction(Action poseActionDraw, string poseDescription, bool hasValidGposeTarget)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine(poseDescription);
        bool isDisabled = false;

        void AddErrorStart(StringBuilder sb)
        {
            sb.Append(ElezenImgui.TooltipSeparator);
            sb.AppendLine("Cannot execute:");
        }

        if (!_dalamudUtilService.IsInGpose)
        {
            if (!isDisabled) AddErrorStart(sb);
            sb.AppendLine("- Requires to be in GPose");
            isDisabled = true;
        }
        if (!hasValidGposeTarget)
        {
            if (!isDisabled) AddErrorStart(sb);
            sb.AppendLine("- Requires a valid GPose target");
            isDisabled = true;
        }
        if (!_charaDataManager.BrioAvailable)
        {
            if (!isDisabled) AddErrorStart(sb);
            sb.AppendLine("- Requires Brio to be installed.");
            isDisabled = true;
        }

        using (ImRaii.Group())
        {
            using var dis = ImRaii.Disabled(isDisabled);
            poseActionDraw.Invoke();
        }
        if (sb.Length > 0)
        {
            ElezenImgui.AttachTooltip(sb.ToString());
        }
    }

    public void DrawPoseData(CharaDataMetaInfoExtendedDto? metaInfo, string actor, bool hasValidGposeTarget)
    {
        if (metaInfo == null || !metaInfo.HasPoses) return;

        bool isInGpose = _dalamudUtilService.IsInGpose;
        var start = ImGui.GetCursorPosX();
        foreach (var item in metaInfo.PoseExtended)
        {
            if (!item.HasPoseData) continue;

            float DrawIcon(float s)
            {
                ImGui.SetCursorPosX(s);
                var posX = ImGui.GetCursorPosX();
                ElezenImgui.ShowIcon(item.HasWorldData ? FontAwesomeIcon.Circle : FontAwesomeIcon.Running);
                if (item.HasWorldData)
                {
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(posX);
                    using var col = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.WindowBg));
                    ElezenImgui.ShowIcon(FontAwesomeIcon.Running);
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(posX);
                    ElezenImgui.ShowIcon(FontAwesomeIcon.Running);
                }
                ImGui.SameLine();
                return ImGui.GetCursorPosX();
            }

            string tooltip = string.IsNullOrEmpty(item.Description) ? "No description set" : "Pose Description: " + item.Description;
            if (!isInGpose)
            {
                start = DrawIcon(start);
                ElezenImgui.AttachTooltip(tooltip + ElezenImgui.TooltipSeparator + (item.HasWorldData ? GetWorldDataTooltipText(item) + ElezenImgui.TooltipSeparator + "Click to show on Map" : string.Empty));
                if (item.HasWorldData && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    PlayerInteractionService.SetMarkerAndOpenMap(item.Position, item.Map);
                }
            }
            else
            {
                tooltip += ElezenImgui.TooltipSeparator + $"Left Click: Apply this pose to {CharaName(actor)}";
                if (item.HasWorldData) tooltip += Environment.NewLine + $"CTRL+Right Click: Apply world position to {CharaName(actor)}."
                        + ElezenImgui.TooltipSeparator + "!!! CAUTION: Applying world position will likely yeet this actor into nirvana. Use at your own risk !!!";
                GposePoseAction(() =>
                {
                    start = DrawIcon(start);
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    {
                        _ = _charaDataManager.ApplyPoseData(item, actor);
                    }
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && ElezenImgui.CtrlPressed())
                    {
                        _ = _charaDataManager.ApplyWorldDataToTarget(item, actor);
                    }
                }, tooltip, hasValidGposeTarget);
                ImGui.SameLine();
            }
        }
        if (metaInfo.PoseExtended.Count != 0) ImGui.NewLine();
    }

    public void DrawUpdateSharedDataButton(CancellationToken token, Action? afterUpdate = null)
    {
        using (ImRaii.Disabled(_charaDataManager.OwnDataDownloading || _charaDataManager.SharedDataOnCooldown))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ArrowCircleDown, "Update Data Shared With You"))
            {
                _ = _charaDataManager.GetAllSharedData(token).ContinueWith(_ => afterUpdate?.Invoke(),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
        if (_charaDataManager.SharedDataOnCooldown)
        {
            ElezenImgui.AttachTooltip("You can only refresh all character data from server every minute. Please wait.");
        }
    }
}
