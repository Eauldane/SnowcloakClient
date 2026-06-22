using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.API.Dto.CharaData;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.CharaData;
using Snowcloak.Services.CharaData.Models;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI.Components;
using Snowcloak.WebAPI;

namespace Snowcloak.UI;

internal sealed partial class CharaDataHubUi : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly CharaDataManager _charaDataManager;
    private readonly CharaDataNearbyManager _charaDataNearbyManager;
    private readonly CharaDataConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly CharaDataHubContext _ctx;
    private readonly CharaDataHubMcdfExportTab _mcdfExportTab;
    private readonly CharaDataHubGposeTogetherTab _gposeTogetherTab;
    private readonly CharaDataHubNearbyPosesTab _nearbyPosesTab;
    private readonly CharaDataHubDataApplicationTab _dataApplicationTab;
    private readonly CharaDataHubMcdOnlineTab _mcdOnlineTab;
    private CancellationTokenSource _closalCts = new();
    private bool _disableUI;
    private readonly CancellationTokenSource _disposalCts = new();
    private Task? _gposeStateRefreshTask;
    private DateTime _lastGposeStateRefresh = DateTime.MinValue;
    private static readonly TimeSpan GposeStateRefreshInterval = TimeSpan.FromMilliseconds(250);
    private DateTime _lastFavoriteUpdateTime = DateTime.UtcNow;

    public CharaDataHubUi(ILogger<CharaDataHubUi> logger, SnowMediator mediator, PerformanceCollectorService performanceCollectorService,
                         CharaDataManager charaDataManager, CharaDataNearbyManager charaDataNearbyManager, CharaDataConfigService configService,
                         CharaDataStateConfigService stateConfigService,
                         ApiController apiController,
                         DalamudUtilService dalamudUtilService, FileDialogManager fileDialogManager, PairManager pairManager,
                         NotesStore notesStore,
                         GposeLobbySession gposeLobbySession)
        : base(logger, mediator, "Snowcloak Character Data Hub###SnowcloakCharaDataUI", performanceCollectorService)
    {
        SetWindowSizeConstraints();

        _charaDataManager = charaDataManager;
        _charaDataNearbyManager = charaDataNearbyManager;
        _configService = configService;
        _dalamudUtilService = dalamudUtilService;
        _ctx = new CharaDataHubContext(charaDataManager, stateConfigService, dalamudUtilService);
        _mcdfExportTab = new CharaDataHubMcdfExportTab(configService, fileDialogManager, charaDataManager);
        _gposeTogetherTab = new CharaDataHubGposeTogetherTab(_ctx, configService, charaDataManager, gposeLobbySession,
            dalamudUtilService, notesStore, apiController);
        _nearbyPosesTab = new CharaDataHubNearbyPosesTab(_ctx, charaDataManager, charaDataNearbyManager, configService,
            notesStore, dalamudUtilService, _disposalCts.Token);
        _dataApplicationTab = new CharaDataHubDataApplicationTab(_ctx, charaDataManager, configService, stateConfigService,
            notesStore, dalamudUtilService, fileDialogManager, _disposalCts.Token);
        _mcdOnlineTab = new CharaDataHubMcdOnlineTab(_ctx, charaDataManager, configService, dalamudUtilService,
            notesStore, pairManager, _disposalCts.Token);
        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen |= _configService.Current.OpenMareHubOnGposeStart);
        Mediator.Subscribe<OpenCharaDataHubWithFilterMessage>(this, (msg) =>
        {
            IsOpen = true;
            _dataApplicationTab.OpenSharedWithOwner(msg.UserData.AliasOrUID);
        });
    }

    private static readonly string[] TopLevelTabs = ["GPose Together", "Data Application", "Data Creation", "Settings"];
    private static readonly string[] DataCreationDisabled = ["Data Creation"];
    private static readonly Dictionary<string, string> DataCreationTooltip =
        new(StringComparer.Ordinal) { ["Data Creation"] = "Cannot use creation tools while having Character Data applied to self." };
    private static readonly string[] ApplicationTabs = ["GPose Actors", "Poses Nearby", "Apply Data"];
    private static readonly string[] GposeActorsDisabled = ["GPose Actors"];
    private static readonly Dictionary<string, string> GposeActorsTooltip =
        new(StringComparer.Ordinal) { ["GPose Actors"] = "Only available in GPose" };
    private static readonly string[] CreationTabs = ["MCD Online", "MCDF Export"];

    private string _topLevelTab = "GPose Together";
    private string _applicationTab = "GPose Actors";
    private string _creationTab = "MCD Online";

    private void DrawApplicationTabs(nint gposeTargetAddress)
    {
        bool inGpose = _dalamudUtilService.IsInGpose;
        _applicationTab = ModernTabBar.Draw("TabsApplicationLevel", ApplicationTabs, _applicationTab,
            inGpose ? null : GposeActorsDisabled,
            inGpose ? null : GposeActorsTooltip);
        ImGuiHelpers.ScaledDummy(3);

        switch (_applicationTab)
        {
            case "GPose Actors":
                using (ImRaii.PushId("gposeControls"))
                {
                    DrawGposeControls(gposeTargetAddress);
                }
                break;

            case "Poses Nearby":
                using (ImRaii.PushId("nearbyPoseControls"))
                {
                    _nearbyPosesTab.Draw();
                }
                break;

            case "Apply Data":
                using (ImRaii.PushId("applyData"))
                {
                    _dataApplicationTab.Draw();
                }
                break;
        }
    }

    private void DrawCreationTabs()
    {
        _creationTab = ModernTabBar.Draw("TabsCreationLevel", CreationTabs, _creationTab);
        ImGuiHelpers.ScaledDummy(3);

        switch (_creationTab)
        {
            case "MCD Online":
                using (ImRaii.PushId("mcdOnline"))
                {
                    _mcdOnlineTab.Draw();
                }
                break;

            case "MCDF Export":
                using (ImRaii.PushId("mcdfExport"))
                {
                    _mcdfExportTab.Draw();
                }
                break;
        }
    }

    public string CharaName(string name) => _ctx.CharaName(name);

    public override void OnClose()
    {
        if (_disableUI)
        {
            IsOpen = true;
            return;
        }

        _closalCts.Cancel();
        _ctx.SelectedDtoId = string.Empty;
        _dataApplicationTab.ResetTransientState();
        _charaDataNearbyManager.ComputeNearbyData = false;
    }

    public override void OnOpen()
    {
        _closalCts.Cancel();
        _closalCts.Dispose();
        _closalCts = new();
        _lastGposeStateRefresh = DateTime.MinValue;
        QueueGposeStateRefresh(force: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closalCts.Cancel();
            _closalCts.Dispose();
            _disposalCts.Cancel();
            _disposalCts.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void DrawInternal()
    {
        _disableUI = _charaDataManager.IsBusy;
        _ctx.DisableUI = _disableUI;
        _ctx.ClosalToken = _closalCts.Token;
        if (DateTime.UtcNow.Subtract(_lastFavoriteUpdateTime).TotalSeconds > 2)
        {
            _lastFavoriteUpdateTime = DateTime.UtcNow;
            _dataApplicationTab.RefreshFavoriteFilter();
        }

        QueueGposeStateRefresh();
        nint gposeTargetAddress = _ctx.GposeTargetAddress;

        if (!_charaDataManager.BrioAvailable)
        {
            ImGuiHelpers.ScaledDummy(3);
            CharaDataHubCard.Warning("Brio is not installed. Posing and spawning characters will be unavailable until you install and enable it.");
            SnowcloakUi.DistanceSeparator();
        }

        using var disabled = ImRaii.Disabled(_disableUI);

        DisableDisabled(() =>
        {
            if (_charaDataManager.DataApplication.IsRunning)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Applying Data to Actor");
                ImGui.SameLine();
                if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Ban, "Cancel Application"))
                {
                    _charaDataManager.CancelDataApplication();
                }
            }
            if (!string.IsNullOrEmpty(_charaDataManager.DataApplicationProgress))
            {
                CharaDataHubCard.Info(_charaDataManager.DataApplicationProgress);
            }
            if (_charaDataManager.DataApplication.IsRunning)
            {
                CharaDataHubCard.Warning("Avoid interacting with this actor while data is being applied — doing so can cause crashes.");
                ImGuiHelpers.ScaledDummy(5);
                ImGui.Separator();
            }
        });

        _ctx.IsHandlingSelf = _charaDataManager.HandledCharaData.Any(c => c.IsSelf);
        if (_ctx.IsHandlingSelf) _ctx.OpenMcdOnlineOnNextRun = false;

        // Programmatic tab selection (replaces the old ImGuiTabItemFlags.SetSelected).
        if (_ctx.OpenDataApplicationShared)
        {
            _topLevelTab = "Data Application";
            _applicationTab = "Apply Data";
        }
        if (_ctx.OpenMcdOnlineOnNextRun)
        {
            _topLevelTab = "Data Creation";
            _creationTab = "MCD Online";
            _ctx.OpenMcdOnlineOnNextRun = false;
        }

        _topLevelTab = ModernTabBar.Draw("TabsTopLevel", TopLevelTabs, _topLevelTab,
            _ctx.IsHandlingSelf ? DataCreationDisabled : null,
            _ctx.IsHandlingSelf ? DataCreationTooltip : null);
        ImGuiHelpers.ScaledDummy(3);

        switch (_topLevelTab)
        {
            case "GPose Together":
                _gposeTogetherTab.Draw();
                break;

            case "Data Application":
                DrawApplicationTabs(gposeTargetAddress);
                break;

            case "Data Creation":
                using (ImRaii.PushId("dataCreation"))
                {
                    DrawCreationTabs();
                }
                break;

            case "Settings":
                using (ImRaii.PushId("settings"))
                {
                    DrawSettings();
                }
                break;
        }

        // Only compute nearby pose data while its tab is actually open.
        _charaDataNearbyManager.ComputeNearbyData =
            string.Equals(_topLevelTab, "Data Application", StringComparison.Ordinal)
            && string.Equals(_applicationTab, "Poses Nearby", StringComparison.Ordinal);

        SetWindowSizeConstraints();
    }

    private void SetWindowSizeConstraints()
    {
        SetScaledSizeConstraints(new Vector2(1000, 500), new Vector2(1000, 2000));
    }

    private void QueueGposeStateRefresh(bool force = false)
    {
        if (_disposalCts.IsCancellationRequested) return;
        if (!force && DateTime.UtcNow - _lastGposeStateRefresh < GposeStateRefreshInterval) return;
        if (_gposeStateRefreshTask != null && !_gposeStateRefreshTask.IsCompleted) return;

        _lastGposeStateRefresh = DateTime.UtcNow;
        _gposeStateRefreshTask = RefreshGposeStateAsync();
    }

    private async Task RefreshGposeStateAsync()
    {
        try
        {
            var target = await _dalamudUtilService.GetGposeTargetGameObjectAsync().ConfigureAwait(false);
            bool hasValidTarget = _dalamudUtilService.IsInGpose && target != null
                && target.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc;
            var targetAddress = target?.Address ?? nint.Zero;
            var targetName = hasValidTarget ? target!.Name.TextValue : "Invalid Target";
            _ctx.SetGposeState(hasValidTarget, targetName, targetAddress);
        }
        catch (Exception ex)
        {
            LogCouldNotRefreshGposeTargetState(_logger, ex);
        }
    }

    private void DrawGposeControls(nint gposeTargetAddress)
    {
        ModernSection.Header(FontAwesomeIcon.Users, "GPose Actors");
        ImGuiHelpers.ScaledDummy(5);
        using var indent = ImRaii.PushIndent(10f);

        foreach (var actor in _dalamudUtilService.GetGposeCharactersFromObjectTable())
        {
            if (actor == null) continue;
            using var actorId = ImRaii.PushId(actor.Name.TextValue);
            ElezenImgui.DrawGrouped(() =>
            {
                if (ElezenImgui.IconButton(FontAwesomeIcon.Crosshairs))
                {
                    unsafe
                    {
                        GposeService.GposeTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)actor.Address;
                    }
                }
                ImGui.SameLine();
                ElezenImgui.AttachTooltip($"Target the GPose Character {CharaName(actor.Name.TextValue)}");
                ImGui.AlignTextToFramePadding();
                var pos = ImGui.GetCursorPosX();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, actor.Address == gposeTargetAddress))
                {
                    ImGui.TextUnformatted(CharaName(actor.Name.TextValue));
                }
                ImGui.SameLine(250);
                var handled = _charaDataManager.HandledCharaData.FirstOrDefault(c => string.Equals(c.Name, actor.Name.TextValue, StringComparison.Ordinal));
                using (ImRaii.Disabled(handled == null))
                {
                    ElezenImgui.ShowIcon(FontAwesomeIcon.InfoCircle);
                    var id = string.IsNullOrEmpty(handled?.MetaInfo.Uploader.UID) ? handled?.MetaInfo.Id : handled.MetaInfo.FullId;
                    ElezenImgui.AttachTooltip($"Applied Data: {id ?? "No data applied"}");

                    ImGui.SameLine();
                    // maybe do this better, check with brio for handled charas or sth
                    using (ImRaii.Disabled(!actor.Name.TextValue.StartsWith("Brio ", StringComparison.Ordinal)))
                    {
                        if (ElezenImgui.IconButton(FontAwesomeIcon.Trash))
                        {
                            _charaDataManager.RemoveChara(actor.Name.TextValue);
                        }
                        ElezenImgui.AttachTooltip($"Remove character {CharaName(actor.Name.TextValue)}");
                    }
                    ImGui.SameLine();
                    if (ElezenImgui.IconButton(FontAwesomeIcon.Undo))
                    {
                        _charaDataManager.RevertChara(handled);
                    }
                    ElezenImgui.AttachTooltip($"Revert applied data from {CharaName(actor.Name.TextValue)}");
                    ImGui.SetCursorPosX(pos);
                    _ctx.DrawPoseData(handled?.MetaInfo, actor.Name.TextValue, true);
                }
            });

            ImGuiHelpers.ScaledDummy(2);
        }
    }

    private void DrawSettings()
    {
        ImGuiHelpers.ScaledDummy(5);
        ModernSection.Header(FontAwesomeIcon.Cog, "Settings");
        ImGuiHelpers.ScaledDummy(5);
        bool openInGpose = _configService.Current.OpenMareHubOnGposeStart;
        if (ImGui.Checkbox("Open Character Data Hub when GPose loads", ref openInGpose))
        {
            _configService.Update(c => c.OpenMareHubOnGposeStart = openInGpose);
        }
        ElezenImgui.DrawHelpText("This will automatically open the import menu when loading into Gpose. If unchecked you can open the menu manually with /mare gpose");
        bool downloadDataOnConnection = _configService.Current.DownloadMcdDataOnConnection;
        if (ImGui.Checkbox("Download MCD Online Data on connecting", ref downloadDataOnConnection))
        {
            _configService.Update(c => c.DownloadMcdDataOnConnection = downloadDataOnConnection);
        }
        ElezenImgui.DrawHelpText("This will automatically download MCD Online data (Your Own and Shared with You) once a connection is established to the server.");

        bool showHelpTexts = _configService.Current.ShowHelpTexts;
        if (ImGui.Checkbox("Show \"What is this? (Explanation / Help)\" foldouts", ref showHelpTexts))
        {
            _configService.Update(c => c.ShowHelpTexts = showHelpTexts);
        }

        bool abbreviate = _ctx.AbbreviateCharaName;
        if (ImGui.Checkbox("Abbreviate Chara Names", ref abbreviate)) _ctx.AbbreviateCharaName = abbreviate;
        ElezenImgui.DrawHelpText("This setting will abbreviate displayed names. This setting is not persistent and will reset between restarts.");

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Last Export Folder");
        ImGui.SameLine(300);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(string.IsNullOrEmpty(_configService.Current.LastSavedCharaDataLocation) ? "Not set" : _configService.Current.LastSavedCharaDataLocation);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Ban, "Clear Last Export Folder"))
        {
            _configService.Update(c => c.LastSavedCharaDataLocation = string.Empty);
        }
        ElezenImgui.DrawHelpText("Use this if the Load or Save MCDF file dialog does not open");
    }

    private void DisableDisabled(Action drawAction)
    {
        if (_disableUI) ImGui.EndDisabled();
        drawAction();
        if (_disableUI) ImGui.BeginDisabled();
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Trace, Message = "Could not refresh GPose target state")]
    private static partial void LogCouldNotRefreshGposeTargetState(ILogger logger, Exception exception);
}
