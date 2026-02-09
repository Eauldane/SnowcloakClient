using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using ElezenTools.UI;
using Snowcloak.API.Data.Extensions;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Utils;
using System.Numerics;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using System.Globalization;

namespace Snowcloak.UI;

public class StandaloneProfileUi : WindowMediatorSubscriberBase
{
    private readonly SnowProfileManager _snowProfileManager;
    private readonly PairManager _pairManager;
    private readonly ProfileVisibility? _requestedVisibility;
    private readonly ServerConfigurationManager _serverManager;
    private readonly UiSharedService _uiSharedService;
    private bool _adjustedForScrollBars = false;
    private byte[] _lastProfilePicture = [];
    private IDalamudTextureWrap? _textureWrap;

    public StandaloneProfileUi(ILogger<StandaloneProfileUi> logger, SnowMediator mediator, UiSharedService uiBuilder,
        ServerConfigurationManager serverManager, SnowProfileManager snowProfileManager, PairManager pairManager, Pair? pair,
        UserData userData, ProfileVisibility? requestedVisibility, PerformanceCollectorService performanceCollector)
        : base(logger, mediator,
            String.Format(CultureInfo.InvariantCulture, $"Profile of {0}", userData.AliasOrUID) +
            "##SnowcloakSyncStandaloneProfileUI" + userData.AliasOrUID + requestedVisibility, performanceCollector)
    {
        _uiSharedService = uiBuilder;
        _serverManager = serverManager;
        _snowProfileManager = snowProfileManager;
        pair ??= pairManager.GetPairByUID(userData.UID);
        Pair = pair;
        UserData = userData;
        _requestedVisibility = requestedVisibility;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize;

        var spacing = ImGui.GetStyle().ItemSpacing;

        Size = new(512 + spacing.X * 3 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, 512);

        IsOpen = true;
    }

    public Pair? Pair { get; }
    public UserData UserData { get; }
    public ProfileVisibility? RequestedVisibility => _requestedVisibility;
    
    protected override void DrawInternal()
    {
        try
        {
            var spacing = ImGui.GetStyle().ItemSpacing;

            var snowProfile = _snowProfileManager.GetSnowProfile(UserData, _requestedVisibility);
            
            var reportLabel = "Report Profile";
            
            if (_textureWrap == null || !snowProfile.ImageData.Value.SequenceEqual(_lastProfilePicture))
            {
                _textureWrap?.Dispose();
                _lastProfilePicture = snowProfile.ImageData.Value;
                _textureWrap = _uiSharedService.LoadImage(_lastProfilePicture);
            }

            var drawList = ImGui.GetWindowDrawList();
            var rectMin = drawList.GetClipRectMin();
            var rectMax = drawList.GetClipRectMax();
            var headerSize = ImGui.GetCursorPosY() - ImGui.GetStyle().WindowPadding.Y;

            using (_uiSharedService.UidFont.Push())
                ElezenImgui.ColouredText(UserData.AliasOrUID, ElezenTools.UI.Colour.HexToVector4(UserData.DisplayColour));

            
            var reportButtonSize = ElezenImgui.GetIconButtonTextSize(FontAwesomeIcon.ExclamationTriangle, reportLabel);
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - reportButtonSize);
            var canReport = Pair != null;
            if (!canReport) ImGui.BeginDisabled();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ExclamationTriangle, reportLabel) && Pair != null)
                Mediator.Publish(new OpenReportPopupMessage(Pair));
            if (!canReport) ImGui.EndDisabled();
            
            ImGuiHelpers.ScaledDummy(new Vector2(spacing.Y, spacing.Y));
            ImGui.Separator();
            var pos = ImGui.GetCursorPos() with { Y = ImGui.GetCursorPosY() - headerSize };
            ImGuiHelpers.ScaledDummy(new Vector2(256, 256 + spacing.Y));
            var postDummy = ImGui.GetCursorPosY();
            ImGui.SameLine();
            var descriptionTextSize = ImGui.CalcTextSize(snowProfile.Description, hideTextAfterDoubleHash: false, 256f);
            var descriptionChildHeight = rectMax.Y - pos.Y - rectMin.Y - spacing.Y * 2;
            if (descriptionTextSize.Y > descriptionChildHeight && !_adjustedForScrollBars)
            {
                Size = Size!.Value with { X = Size.Value.X + ImGui.GetStyle().ScrollbarSize };
                _adjustedForScrollBars = true;
            }
            else if (descriptionTextSize.Y < descriptionChildHeight && _adjustedForScrollBars)
            {
                Size = Size!.Value with { X = Size.Value.X - ImGui.GetStyle().ScrollbarSize };
                _adjustedForScrollBars = false;
            }
            var childFrame = ImGuiHelpers.ScaledVector2(256 + ImGui.GetStyle().WindowPadding.X + ImGui.GetStyle().WindowBorderSize, descriptionChildHeight);
            childFrame = childFrame with
            {
                X = childFrame.X + (_adjustedForScrollBars ? ImGui.GetStyle().ScrollbarSize : 0),
                Y = childFrame.Y / ImGuiHelpers.GlobalScale
            };
            if (ImGui.BeginChildFrame(1000, childFrame))
            {
                using var _ = _uiSharedService.GameFont.Push();
                _uiSharedService.RenderBbCode(snowProfile.Description, ImGui.GetContentRegionAvail().X);
            }
            ImGui.EndChildFrame();

            ImGui.SetCursorPosY(postDummy);
            var note = _serverManager.GetNoteForUid(UserData.UID);
            if (!string.IsNullOrEmpty(note))
            {
                ElezenImgui.ColouredText(note, ImGuiColors.DalamudGrey);
            }
            
            if (Pair != null)
            {
                string status = Pair.IsVisible
                    ? "Visible"
                    : (Pair.IsOnline
                        ? "Online"
                        : "Offline");
                ElezenImgui.ColouredText(status, (Pair.IsVisible || Pair.IsOnline) ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
                if (Pair.IsVisible)
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"({Pair.PlayerName})");
                }
                if (Pair.UserPair != null)
                {
                    ImGui.TextUnformatted("Directly paired");
                    if (Pair.UserPair.OwnPermissions.IsPaused())
                    {
                        ImGui.SameLine();
                        ElezenImgui.ColouredText("You: paused", ImGuiColors.DalamudYellow);
                    }
                    if (Pair.UserPair.OtherPermissions.IsPaused())
                    {
                        ImGui.SameLine();
                        ElezenImgui.ColouredText("They: paused", ImGuiColors.DalamudYellow);
                    }
                }
            

                if (Pair.GroupPair.Any())
                {
                    ImGui.TextUnformatted("Paired through Syncshells:");
                    foreach (var groupPair in Pair.GroupPair.Select(k => k.Key))
                    {
                        var groupNote = _serverManager.GetNoteForGid(groupPair.GID);
                        var groupName = groupPair.GroupAliasOrGID;
                        var groupString = string.IsNullOrEmpty(groupNote) ? groupName : $"{groupNote} ({groupName})";
                        ImGui.TextColored(ElezenTools.UI.Colour.HexToVector4(groupPair.Group.DisplayColour), "- " + groupString);
                    }
                }
            }
            else
            {
                ElezenImgui.ColouredWrappedText("No pairing context available for this user.", ImGuiColors.DalamudGrey);
            }

            if (_textureWrap != null)
            {
                var padding = ImGui.GetStyle().WindowPadding.X / 2;
                bool tallerThanWide = _textureWrap.Height >= _textureWrap.Width;
                var stretchFactor = tallerThanWide ? 256f * ImGuiHelpers.GlobalScale / _textureWrap.Height : 256f * ImGuiHelpers.GlobalScale / _textureWrap.Width;
                var newWidth = _textureWrap.Width * stretchFactor;
                var newHeight = _textureWrap.Height * stretchFactor;
                var remainingWidth = (256f * ImGuiHelpers.GlobalScale - newWidth) / 2f;
                var remainingHeight = (256f * ImGuiHelpers.GlobalScale - newHeight) / 2f;
                drawList.AddImage(_textureWrap.Handle, new Vector2(rectMin.X + padding + remainingWidth, rectMin.Y + spacing.Y + pos.Y + remainingHeight),
                    new Vector2(rectMin.X + padding + remainingWidth + newWidth, rectMin.Y + spacing.Y + pos.Y + remainingHeight + newHeight));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during draw tooltip");
        }
    }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }
}