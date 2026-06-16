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
using Snowcloak.WebAPI.Files;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class PopoutProfileUi : WindowMediatorSubscriberBase, IStaticWindow
{
    private readonly SnowProfileManager _snowProfileManager;
    private readonly TextureService _textureService;
    private readonly ImageTransferService _imageTransferService;
    private readonly ProfileViewComponent _profileView;
    private Vector2 _lastMainPos;
    private Vector2 _lastMainSize;
    private byte[] _lastHeaderImage = [];
    private byte[] _lastProfilePicture = [];
    private Pair? _pair;
    private IDalamudTextureWrap? _headerTextureWrap;
    private IDalamudTextureWrap? _textureWrap;

    public PopoutProfileUi(ILogger<PopoutProfileUi> logger, SnowMediator mediator, UiFontService fontService,
        BbCodeRenderService bbCodeRenderService, TextureService textureService,
        SnowcloakConfigService snowcloakConfigService, SnowProfileManager snowProfileManager,
        ImageTransferService imageTransferService, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak: RP Card###SnowcloakSyncPopoutProfileUI", performanceCollectorService)
    {
        _textureService = textureService;
        _imageTransferService = imageTransferService;
        _profileView = new ProfileViewComponent(fontService, bbCodeRenderService, textureService);
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
            RefreshHeaderTexture(ResolveImageBytes(profile.Document.HeaderImageHash));
            RefreshTexture(ResolveImageBytes(profile.Document.ProfilePictureHash));
            var moodlesData = _pair.HasAnyConnection() ? _pair.LastReceivedCharacterData?.MoodlesData : null;
            _profileView.DrawCompact(new ProfileViewRequest(
                profile,
                ResolveFallbackName(_pair),
                _headerTextureWrap,
                _textureWrap,
                GetVisibleTagsForViewer(profile),
                moodlesData,
                "popout-profile"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not draw RP profile card");
        }
    }

    private void RefreshTexture(byte[] bytes)
    {
        if (_textureWrap != null && bytes.SequenceEqual(_lastProfilePicture)) return;
        _textureWrap?.Dispose();
        _lastProfilePicture = bytes;
        _textureWrap = bytes.Length == 0 ? null : _textureService.LoadImage(bytes);
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
        _headerTextureWrap = bytes.Length == 0 ? null : _textureService.LoadImage(bytes);
    }

    protected override void Dispose(bool disposing)
    {
        _headerTextureWrap?.Dispose();
        _textureWrap?.Dispose();
        base.Dispose(disposing);
    }

    private byte[] ResolveImageBytes(string? hash)
        => _imageTransferService.TryGetImage(hash, out var bytes) ? bytes : [];

    private static string ResolveFallbackName(Pair pair)
    {
        if (pair.UserPair == null)
            return pair.IsVisible && !string.IsNullOrWhiteSpace(pair.PlayerName) ? pair.PlayerName : "Unknown character";
        return !string.IsNullOrWhiteSpace(pair.PlayerName) ? pair.PlayerName : pair.UserData.AliasOrUID;
    }
}
