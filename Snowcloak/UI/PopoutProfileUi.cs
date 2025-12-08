using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Snowcloak.API.Data.Extensions;
using Microsoft.Extensions.Logging;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.Configuration;
using Snowcloak.Utils;
using System.Numerics;

namespace Snowcloak.UI;

public class PopoutProfileUi : WindowMediatorSubscriberBase
{
    private readonly SnowProfileManager _snowProfileManager;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly UiSharedService _uiSharedService;
    private Vector2 _lastMainPos = Vector2.Zero;
    private Vector2 _lastMainSize = Vector2.Zero;
    private byte[] _lastProfilePicture = [];
    private byte[] _lastSupporterPicture = [];
    private Pair? _pair;
    private IDalamudTextureWrap? _supporterTextureWrap;
    private IDalamudTextureWrap? _textureWrap;

    public PopoutProfileUi(ILogger<PopoutProfileUi> logger, SnowMediator mediator, UiSharedService uiSharedService,
        ServerConfigurationManager serverManager, SnowcloakConfigService snowcloakConfigService,
        SnowProfileManager snowProfileManager, PairManager pairManager, PerformanceCollectorService performanceCollectorService) : base(logger, mediator, "###SnowcloakSyncPopoutProfileUI", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _serverManager = serverManager;
        _snowProfileManager = snowProfileManager;
        _pairManager = pairManager;
        Flags = ImGuiWindowFlags.NoDecoration;

        Mediator.Subscribe<ProfilePopoutToggle>(this, (msg) =>
        {
            IsOpen = msg.Pair != null;
            _pair = msg.Pair;
            _lastProfilePicture = [];
            _lastSupporterPicture = [];
            _textureWrap?.Dispose();
            _textureWrap = null;
            _supporterTextureWrap?.Dispose();
            _supporterTextureWrap = null;
        });

        Mediator.Subscribe<CompactUiChange>(this, (msg) =>
        {
            if (msg.Size != Vector2.Zero)
            {
                var border = ImGui.GetStyle().WindowBorderSize;
                var padding = ImGui.GetStyle().WindowPadding;
                Size = new(256 + (padding.X * 2) + border, msg.Size.Y / ImGuiHelpers.GlobalScale);
                _lastMainSize = msg.Size;
            }
            var mainPos = msg.Position == Vector2.Zero ? _lastMainPos : msg.Position;
            if (snowcloakConfigService.Current.ProfilePopoutRight)
            {
                Position = new(mainPos.X + _lastMainSize.X * ImGuiHelpers.GlobalScale, mainPos.Y);
            }
            else
            {
                Position = new(mainPos.X - Size!.Value.X * ImGuiHelpers.GlobalScale, mainPos.Y);
            }

            if (msg.Position != Vector2.Zero)
            {
                _lastMainPos = msg.Position;
            }
        });

        IsOpen = false;
    }

    protected override void DrawInternal()
    {
        if (_pair == null) return;

        try
        {
            var spacing = ImGui.GetStyle().ItemSpacing;

            var snowProfile = _snowProfileManager.GetSnowProfile(_pair.UserData);

            if (_textureWrap == null || !snowProfile.ImageData.Value.SequenceEqual(_lastProfilePicture))
            {
                _textureWrap?.Dispose();
                _lastProfilePicture = snowProfile.ImageData.Value;

                _textureWrap = _uiSharedService.LoadImage(_lastProfilePicture);
            }

            var drawList = ImGui.GetWindowDrawList();
            var rectMin = drawList.GetClipRectMin();
            var rectMax = drawList.GetClipRectMax();

            using (_uiSharedService.UidFont.Push())
                UiSharedService.ColorText(_pair.UserData.AliasOrUID, Colours.Hex2Vector4(_pair.UserData.DisplayColour));

            ImGuiHelpers.ScaledDummy(spacing.Y, spacing.Y);
            var textPos = ImGui.GetCursorPosY();
            ImGui.Separator();
            var imagePos = ImGui.GetCursorPos();
            ImGuiHelpers.ScaledDummy(256, 256 * ImGuiHelpers.GlobalScale + spacing.Y);
            var note = _serverManager.GetNoteForUid(_pair.UserData.UID);
            if (!string.IsNullOrEmpty(note))
            {
                UiSharedService.ColorText(note, ImGuiColors.DalamudGrey);
            }
            string status = _pair.IsVisible ? "Visible" : (_pair.IsOnline ? "Online" : "Offline");
            UiSharedService.ColorText(status, (_pair.IsVisible || _pair.IsOnline) ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed);
            if (_pair.IsVisible)
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"({_pair.PlayerName})");
            }
            if (_pair.UserPair != null)
            {
                ImGui.TextUnformatted("Directly paired");
                if (_pair.UserPair.OwnPermissions.IsPaused())
                {
                    ImGui.SameLine();
                    UiSharedService.ColorText("You: paused", ImGuiColors.DalamudYellow);
                }
                if (_pair.UserPair.OtherPermissions.IsPaused())
                {
                    ImGui.SameLine();
                    UiSharedService.ColorText("They: paused", ImGuiColors.DalamudYellow);
                }
            }
            if (_pair.GroupPair.Any())
            {
                ImGui.TextUnformatted("Paired through Syncshells:");
                foreach (var groupPair in _pair.GroupPair.Select(k => k.Key))
                {
                    var groupNote = _serverManager.GetNoteForGid(groupPair.GID);
                    var groupName = groupPair.GroupAliasOrGID;
                    var groupString = string.IsNullOrEmpty(groupNote) ? groupName : $"{groupNote} ({groupName})";
                    ImGui.TextColored(Colours.Hex2Vector4(groupPair.Group.DisplayColour), "- " + groupString);
                }
            }

            ImGui.Separator();
            _uiSharedService.GameFont.Push();
            var remaining = ImGui.GetWindowContentRegionMax().Y - ImGui.GetCursorPosY();
            var descriptionHeight = Math.Max(remaining, 120f);
            if (ImGui.BeginChild("##popout-description", new Vector2(ImGui.GetContentRegionAvail().X, descriptionHeight), true))
            {
                _uiSharedService.RenderBbCode(snowProfile.Description, ImGui.GetContentRegionAvail().X);
            }
            ImGui.EndChild();
            
            _uiSharedService.GameFont.Pop();

            var padding = ImGui.GetStyle().WindowPadding.X / 2;
            bool tallerThanWide = _textureWrap.Height >= _textureWrap.Width;
            var stretchFactor = tallerThanWide ? 256f * ImGuiHelpers.GlobalScale / _textureWrap.Height : 256f * ImGuiHelpers.GlobalScale / _textureWrap.Width;
            var newWidth = _textureWrap.Width * stretchFactor;
            var newHeight = _textureWrap.Height * stretchFactor;
            var remainingWidth = (256f * ImGuiHelpers.GlobalScale - newWidth) / 2f;
            var remainingHeight = (256f * ImGuiHelpers.GlobalScale - newHeight) / 2f;
            drawList.AddImage(_textureWrap.Handle, new Vector2(rectMin.X + padding + remainingWidth, rectMin.Y + spacing.Y + imagePos.Y + remainingHeight),
                new Vector2(rectMin.X + padding + remainingWidth + newWidth, rectMin.Y + spacing.Y + imagePos.Y + remainingHeight + newHeight));
            if (_supporterTextureWrap != null)
            {
                const float iconSize = 38;
                drawList.AddImage(_supporterTextureWrap.Handle,
                    new Vector2(rectMax.X - iconSize - spacing.X, rectMin.Y + (textPos / 2) - (iconSize / 2)),
                    new Vector2(rectMax.X - spacing.X, rectMin.Y + iconSize + (textPos / 2) - (iconSize / 2)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during draw tooltip");
        }
    }
}