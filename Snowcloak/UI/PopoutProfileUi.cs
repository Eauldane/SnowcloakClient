using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class PopoutProfileUi : WindowMediatorSubscriberBase
{
    private readonly SnowProfileManager _snowProfileManager;
    private readonly UiSharedService _uiSharedService;
    private Vector2 _lastMainPos;
    private Vector2 _lastMainSize;
    private byte[] _lastHeaderImage = [];
    private byte[] _lastProfilePicture = [];
    private Pair? _pair;
    private IDalamudTextureWrap? _headerTextureWrap;
    private IDalamudTextureWrap? _textureWrap;

    public PopoutProfileUi(ILogger<PopoutProfileUi> logger, SnowMediator mediator, UiSharedService uiSharedService,
        SnowcloakConfigService snowcloakConfigService, SnowProfileManager snowProfileManager,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak: RP Card###SnowcloakSyncPopoutProfileUI", performanceCollectorService)
    {
        _uiSharedService = uiSharedService;
        _snowProfileManager = snowProfileManager;
        Flags = ImGuiWindowFlags.NoDecoration;
        Mediator.Subscribe<ProfilePopoutToggle>(this, message =>
        {
            IsOpen = message.Pair != null;
            _pair = message.Pair;
            RefreshTexture([]);
        });
        Mediator.Subscribe<CompactUiChange>(this, message =>
        {
            if (message.Size != Vector2.Zero)
            {
                var padding = ImGui.GetStyle().WindowPadding;
                Size = new Vector2(290f + padding.X * 2f, message.Size.Y / ImGuiHelpers.GlobalScale);
                _lastMainSize = message.Size;
            }
            var mainPos = message.Position == Vector2.Zero ? _lastMainPos : message.Position;
            Position = snowcloakConfigService.Current.ProfilePopoutRight
                ? new Vector2(mainPos.X + _lastMainSize.X * ImGuiHelpers.GlobalScale, mainPos.Y)
                : new Vector2(mainPos.X - Size!.Value.X * ImGuiHelpers.GlobalScale, mainPos.Y);
            if (message.Position != Vector2.Zero)
                _lastMainPos = message.Position;
        });
        IsOpen = false;
    }

    protected override void DrawInternal()
    {
        if (_pair == null) return;
        try
        {
            var profile = _snowProfileManager.GetSnowProfile(_pair);
            RefreshHeaderTexture(DecodeImage(profile.Document.HeaderImageBase64));
            RefreshTexture(profile.ImageData.Value);
            CharacterProfileUiShared.DrawHeader(profile.Document, ResolveFallbackName(_pair), compact: true, headerImageTexture: _headerTextureWrap);
            CharacterProfileUiShared.DrawProfileBadges(profile.Document, "popout-profile-badges");

            if (_pair.HasAnyConnection())
                CharacterProfileUiShared.DrawMoodles(_pair.LastReceivedCharacterData?.MoodlesData, "popout-profile", _uiSharedService, maxVisible: 6);

            if (profile.Revision <= 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, string.IsNullOrWhiteSpace(profile.DisabledReason)
                    ? "This character has not published a profile yet."
                    : profile.DisabledReason);
                return;
            }

            if (profile.Disabled)
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, profile.DisabledReason);
                return;
            }

            DrawPortrait();
            if (!string.IsNullOrWhiteSpace(profile.Document.Tagline))
                ImGui.TextWrapped(profile.Document.Tagline);
            CharacterProfileUiShared.DrawLabelValue("Approach:", profile.Document.Approachability);

            foreach (var glance in profile.Document.AtAGlance.Take(3))
                ImGui.BulletText(glance);

            var visibleTags = GetVisibleTagsForViewer(profile);
            if (visibleTags.Count > 0)
            {
                CharacterProfileUiShared.DrawSectionTitle("Tags");
                _ = ProfileTagChipRenderer.DrawTagChips(visibleTags, "popout-rp-tags");
            }

            CharacterProfileUiShared.DrawSectionTitle("Overview");
            using var font = _uiSharedService.GameFont.Push();
            _uiSharedService.RenderBbCode(profile.Description, ImGui.GetContentRegionAvail().X);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not draw RP profile card");
        }
    }

    private void DrawPortrait()
    {
        if (_textureWrap == null) return;
        var max = 240f * ImGuiHelpers.GlobalScale;
        var scale = max / MathF.Max(_textureWrap.Width, _textureWrap.Height);
        ImGui.Image(_textureWrap.Handle, new Vector2(_textureWrap.Width * scale, _textureWrap.Height * scale));
    }

    private void RefreshTexture(byte[] bytes)
    {
        if (_textureWrap != null && bytes.SequenceEqual(_lastProfilePicture)) return;
        _textureWrap?.Dispose();
        _lastProfilePicture = bytes;
        _textureWrap = bytes.Length == 0 ? null : _uiSharedService.LoadImage(bytes);
    }

    private IReadOnlyList<UserProfileTagDto> GetVisibleTagsForViewer(SnowProfileData profile)
    {
        if (profile.IsOwnProfile)
            return ProfileTagUtilities.NormalizeForStorage(profile.Tags);

        var ownProfile = _snowProfileManager.GetOwnProfile(ProfileVisibility.Private);
        var viewerTags = ownProfile.Revision > 0 ? ownProfile.Tags : [];
        return ProfileTagUtilities.GetVisibleTagsForViewer(profile.Tags, viewerTags);
    }

    private void RefreshHeaderTexture(byte[] bytes)
    {
        if (_headerTextureWrap != null && bytes.SequenceEqual(_lastHeaderImage)) return;
        _headerTextureWrap?.Dispose();
        _lastHeaderImage = bytes;
        _headerTextureWrap = bytes.Length == 0 ? null : _uiSharedService.LoadImage(bytes);
    }

    protected override void Dispose(bool disposing)
    {
        _headerTextureWrap?.Dispose();
        _textureWrap?.Dispose();
        base.Dispose(disposing);
    }

    private static byte[] DecodeImage(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return [];
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static string ResolveFallbackName(Pair pair)
    {
        if (pair.UserPair == null)
            return pair.IsVisible && !string.IsNullOrWhiteSpace(pair.PlayerName) ? pair.PlayerName : "Unknown character";
        return !string.IsNullOrWhiteSpace(pair.PlayerName) ? pair.PlayerName : pair.UserData.AliasOrUID;
    }
}
