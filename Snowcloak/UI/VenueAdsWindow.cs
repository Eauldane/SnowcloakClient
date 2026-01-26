using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.Venue;
using Snowcloak.API.Routes;
using Snowcloak.FileCache;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Utils;
using Snowcloak.WebAPI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.SignalR.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;

namespace Snowcloak.UI;

public sealed class VenueAdsWindow : WindowMediatorSubscriberBase
{
    private enum AdsTab
    {
        Browse,
        Manage
    }

    private const int MaxAdTextLength = 600;
    private const int BannerWidth = 720;
    private const int BannerHeight = 300;

    private readonly ApiController _apiController;
    private readonly UiSharedService _uiSharedService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly FileCacheManager _fileCacheManager;
    private readonly FileUploadManager _fileUploadManager;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly Dictionary<string, IDalamudTextureWrap> _bannerTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _bannerDownloadTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _bannerBytes = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<VenueRegistryEntryDto> _browseVenues = [];
    private readonly List<VenueRegistryEntryDto> _ownedVenues = [];
    private int _selectedOwnedVenueIndex = -1;
    private Guid? _selectedAdId;
    private string _adText = string.Empty;
    private string? _bannerHash;
    private int? _bannerWidth;
    private int? _bannerHeight;
    private bool _adIsActive = true;
    private bool _bannerNeedsUpload;
    private bool _isSaving;
    private bool _isLoadingBrowse;
    private bool _isLoadingOwned;
    private string? _statusMessage;
    private bool _statusIsError;
    private AdsTab? _requestedTab;
    private bool _openCreateOnNextDraw;

    public VenueAdsWindow(ILogger<VenueAdsWindow> logger, SnowMediator mediator, UiSharedService uiSharedService,
        ApiController apiController, FileDialogManager fileDialogManager, FileCacheManager fileCacheManager,
        FileUploadManager fileUploadManager, FileTransferOrchestrator fileTransferOrchestrator,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Venue Ads###SnowcloakVenueAds", performanceCollectorService)
    {
        _apiController = apiController;
        _uiSharedService = uiSharedService;
        _fileDialogManager = fileDialogManager;
        _fileCacheManager = fileCacheManager;
        _fileUploadManager = fileUploadManager;
        _fileTransferOrchestrator = fileTransferOrchestrator;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(650, 500)
        };

        Mediator.Subscribe<OpenVenueAdsWindowMessage>(this, (msg) =>
        {
            _requestedTab = msg.OpenCreate ? AdsTab.Manage : AdsTab.Browse;
            _openCreateOnNextDraw = msg.OpenCreate;
            IsOpen = true;
        });
    }

    public override void OnOpen()
    {
        _ = RefreshBrowseAsync();
        _ = RefreshOwnedAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var texture in _bannerTextures.Values)
            {
                texture.Dispose();
            }
            _bannerTextures.Clear();
        }
        base.Dispose(disposing);
    }

    protected override void DrawInternal()
    {
        DrawHeader();

        if (_apiController.ServerState is not ServerState.Connected)
        {
            UiSharedService.ColorTextWrapped("Connect to Snowcloak to browse or manage venue ads.", ImGuiColors.DalamudRed);
            return;
        }

        using var tabs = ImRaii.TabBar("VenueAdsTabs");
        var browseFlags = _requestedTab == AdsTab.Browse ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var manageFlags = _requestedTab == AdsTab.Manage ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

        using (var browseTab = ImRaii.TabItem("Browse Ads", browseFlags))
        {
            if (browseTab)
            {
                DrawBrowseTab();
            }
        }

        using (var manageTab = ImRaii.TabItem("Manage Your Ads", manageFlags))
        {
            if (manageTab)
            {
                DrawManageTab();
            }
        }

        _requestedTab = null;
    }

    private void DrawHeader()
    {
        _uiSharedService.BigText("Venue Advertisements");
        UiSharedService.ColorTextWrapped("Browse active venue ads or manage your own listings.", ImGuiColors.DalamudGrey);
        ImGui.Separator();

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.SyncAlt, "Refresh"))
        {
            _ = RefreshBrowseAsync();
            _ = RefreshOwnedAsync();
        }
        UiSharedService.AttachToolTip("Reload ads and owned venues.");

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            var color = _statusIsError ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen;
            UiSharedService.ColorTextWrapped(_statusMessage, color);
        }
        ImGuiHelpers.ScaledDummy(4);
    }

    private void DrawBrowseTab()
    {
        if (_isLoadingBrowse)
        {
            UiSharedService.ColorTextWrapped("Loading ads...", ImGuiColors.DalamudGrey);
            return;
        }

        if (_browseVenues.Count == 0)
        {
            UiSharedService.ColorTextWrapped("No active venue ads were found.", ImGuiColors.DalamudGrey);
            return;
        }

        using var child = ImRaii.Child("VenueAdsBrowse", new Vector2(-1, -1), false);
        foreach (var venue in _browseVenues)
        {
            var activeAds = venue.Advertisements?
                .Where(ad => ad.IsActive ?? true)
                .ToList() ?? [];
            if (activeAds.Count == 0)
                continue;

            var headerLabel = venue.VenueName ?? "Venue";
            var headerOpen = ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
            if (!headerOpen)
                continue;

            if (!string.IsNullOrWhiteSpace(venue.VenueDescription))
            {
                UiSharedService.ColorTextWrapped(venue.VenueDescription, ImGuiColors.DalamudGrey);
            }

            foreach (var ad in activeAds)
            {
                DrawAdCard(ad);
                ImGui.Separator();
            }
        }
    }

    private void DrawManageTab()
    {
        if (_isLoadingOwned)
        {
            UiSharedService.ColorTextWrapped("Loading owned venues...", ImGuiColors.DalamudGrey);
            return;
        }

        if (_ownedVenues.Count == 0)
        {
            UiSharedService.ColorTextWrapped("No owned venues were found. Register a venue before creating ads.", ImGuiColors.DalamudGrey);
            return;
        }

        if (_openCreateOnNextDraw)
        {
            _openCreateOnNextDraw = false;
            SelectOwnedVenue(_selectedOwnedVenueIndex < 0 ? 0 : _selectedOwnedVenueIndex);
            ResetAdEditor();
        }

        DrawOwnedVenueSelector();
        UiSharedService.DistanceSeparator();

        DrawOwnedAdsList();
        UiSharedService.DistanceSeparator();
        DrawAdEditor();
    }

    private void DrawOwnedVenueSelector()
    {
        ImGui.SetNextItemWidth(360 * ImGuiHelpers.GlobalScale);
        var label = _selectedOwnedVenueIndex >= 0 && _selectedOwnedVenueIndex < _ownedVenues.Count
            ? _ownedVenues[_selectedOwnedVenueIndex].VenueName ?? "Venue"
            : "Select a venue...";
        if (ImGui.BeginCombo("Owned Venue", label))
        {
            for (var i = 0; i < _ownedVenues.Count; i++)
            {
                var entry = _ownedVenues[i];
                if (ImGui.Selectable(entry.VenueName ?? "Venue", i == _selectedOwnedVenueIndex))
                {
                    SelectOwnedVenue(i);
                }
            }
            ImGui.EndCombo();
        }
    }

    private void DrawOwnedAdsList()
    {
        if (_selectedOwnedVenueIndex < 0 || _selectedOwnedVenueIndex >= _ownedVenues.Count)
            return;

        var venue = _ownedVenues[_selectedOwnedVenueIndex];
        _uiSharedService.BigText("Existing Ads");

        if (venue.Advertisements == null || venue.Advertisements.Count == 0)
        {
            UiSharedService.ColorTextWrapped("No ads yet. Create one below.", ImGuiColors.DalamudGrey);
            return;
        }

        using var listChild = ImRaii.Child("OwnedAdsList", new Vector2(-1, 140), true);
        for (var i = 0; i < venue.Advertisements.Count; i++)
        {
            var ad = venue.Advertisements[i];
            var isActive = ad.IsActive ?? true;
            var label = $"{(isActive ? "Active" : "Inactive")} ad {(string.IsNullOrWhiteSpace(ad.Text) ? "(Banner only)" : "(Text)" )}";
            if (ImGui.Selectable(label, _selectedAdId == ad.Id))
            {
                LoadAdForEdit(ad);
            }
        }
    }

    private void DrawAdEditor()
    {
        _uiSharedService.BigText("Ad Editor");
        ImGui.TextUnformatted($"Text ({_adText.Length}/{MaxAdTextLength})");
        using (_uiSharedService.GameFont.Push())
        {
            ImGui.InputTextMultiline("##VenueAdText", ref _adText, MaxAdTextLength, ImGuiHelpers.ScaledVector2(-1, 140));
        }

        ImGuiHelpers.ScaledDummy(4);
        ImGui.TextUnformatted($"Banner ({BannerWidth}x{BannerHeight})");
        DrawBannerPreview();

        if (_uiSharedService.IconTextButton(FontAwesomeIcon.FileUpload, "Upload banner"))
        {
            _fileDialogManager.OpenFileDialog("Select banner image", ".png", (success, file) =>
            {
                if (!success)
                    return;

                _ = Task.Run(async () => await HandleBannerSelectionAsync(file).ConfigureAwait(false));
            });
        }
        UiSharedService.AttachToolTip("Upload a 720x300 PNG banner.");

        ImGui.SameLine();
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Clear banner"))
        {
            ClearBanner();
        }
        UiSharedService.AttachToolTip("Remove the banner from this ad.");

        ImGuiHelpers.ScaledDummy(4);
        ImGui.Checkbox("Ad is active", ref _adIsActive);

        var validationMessage = ValidateAd();
        if (!string.IsNullOrWhiteSpace(validationMessage))
        {
            UiSharedService.ColorTextWrapped(validationMessage, ImGuiColors.DalamudRed);
        }

        ImGui.BeginDisabled(_isSaving || !string.IsNullOrWhiteSpace(validationMessage));
        if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, _selectedAdId.HasValue ? "Save Ad" : "Create Ad"))
        {
            _ = SaveAdAsync();
        }
        ImGui.EndDisabled();

        if (_selectedAdId.HasValue)
        {
            ImGui.SameLine();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Delete Ad"))
            {
                _ = DeleteAdAsync();
            }
        }
    }

    private void DrawAdCard(VenueAdvertisementDto ad)
    {
        if (!string.IsNullOrWhiteSpace(ad.Text))
        {
            ImGui.TextWrapped(ad.Text);
        }

        if (!string.IsNullOrWhiteSpace(ad.BannerFileHash))
        {
            DrawBannerImage(ad.BannerFileHash, ad.BannerWidth, ad.BannerHeight);
        }
    }

    private void DrawBannerPreview()
    {
        if (!string.IsNullOrWhiteSpace(_bannerHash))
        {
            DrawBannerImage(_bannerHash!, _bannerWidth, _bannerHeight);
            return;
        }

        UiSharedService.ColorTextWrapped("No banner selected.", ImGuiColors.DalamudGrey);
    }

    private void DrawBannerImage(string bannerHash, int? width, int? height)
    {
        if (_bannerTextures.TryGetValue(bannerHash, out var texture))
        {
            var availableWidth = Math.Max(0f, ImGui.GetContentRegionAvail().X);
            var targetWidth = width ?? BannerWidth;
            var targetHeight = height ?? BannerHeight;
            var scale = availableWidth > 0 ? Math.Min(1f, availableWidth / targetWidth) : 1f;
            var size = new Vector2(targetWidth * scale, targetHeight * scale);
            ImGui.Image(texture.Handle, size);
            return;
        }

        EnsureBannerBytesRequested(bannerHash);
        if (_bannerBytes.TryGetValue(bannerHash, out var bytes))
        {
            try
            {
                var tex = _uiSharedService.LoadImage(bytes);
                _bannerTextures[bannerHash] = tex;
                _bannerBytes.TryRemove(bannerHash, out _);
                DrawBannerImage(bannerHash, width, height);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load banner image {hash}", bannerHash);
                UiSharedService.ColorTextWrapped("Failed to load banner image.", ImGuiColors.DalamudRed);
            }
            return;
        }

        UiSharedService.ColorTextWrapped("Loading banner...", ImGuiColors.DalamudGrey);
    }

    private void EnsureBannerBytesRequested(string bannerHash)
    {
        if (_bannerDownloadTasks.ContainsKey(bannerHash))
            return;

        _bannerDownloadTasks[bannerHash] = Task.Run(async () =>
        {
            var bytes = await LoadBannerBytesAsync(bannerHash).ConfigureAwait(false);
            if (bytes != null)
            {
                _bannerBytes[bannerHash] = bytes;
            }
            _bannerDownloadTasks.TryRemove(bannerHash, out _);
            return bytes;
        });
    }

    private async Task<byte[]?> LoadBannerBytesAsync(string bannerHash)
    {
        try
        {
            var cacheEntry = _fileCacheManager.GetFileCacheByHash(bannerHash);
            if (cacheEntry != null && File.Exists(cacheEntry.ResolvedFilepath))
            {
                return await File.ReadAllBytesAsync(cacheEntry.ResolvedFilepath).ConfigureAwait(false);
            }

            if (_fileTransferOrchestrator.FilesCdnUri == null)
                return null;

            var uri = SnowFiles.DistributionGetFullPath(_fileTransferOrchestrator.FilesCdnUri, bannerHash);
            var response = await _fileTransferOrchestrator.SendRequestAsync(HttpMethod.Get, uri).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download banner {hash}", bannerHash);
            return null;
        }
    }

    private void SelectOwnedVenue(int index)
    {
        if (index < 0 || index >= _ownedVenues.Count)
            return;

        _selectedOwnedVenueIndex = index;
        ResetAdEditor();
    }

    private void ResetAdEditor()
    {
        _selectedAdId = null;
        _adText = string.Empty;
        _bannerHash = null;
        _bannerWidth = null;
        _bannerHeight = null;
        _adIsActive = true;
        _bannerNeedsUpload = false;
        _statusMessage = null;
    }

    private void LoadAdForEdit(VenueAdvertisementDto ad)
    {
        _selectedAdId = ad.Id;
        _adText = ad.Text ?? string.Empty;
        _bannerHash = ad.BannerFileHash;
        _bannerWidth = ad.BannerWidth;
        _bannerHeight = ad.BannerHeight;
        _adIsActive = ad.IsActive ?? true;
        _bannerNeedsUpload = false;
        _statusMessage = null;
    }

    private string? ValidateAd()
    {
        var text = _adText.Trim();
        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasBanner = !string.IsNullOrWhiteSpace(_bannerHash);

        if (!hasText && !hasBanner)
            return "Ads must include text or a banner image.";

        if (_adText.Length > MaxAdTextLength)
            return "Ad text is too long.";

        if (hasBanner && (_bannerWidth != BannerWidth || _bannerHeight != BannerHeight))
            return $"Banner must be exactly {BannerWidth}x{BannerHeight}.";

        return null;
    }

    private async Task HandleBannerSelectionAsync(string filePath)
    {
        try
        {
            var fileBytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            using var ms = new MemoryStream(fileBytes);
            var dimensions = PngHdr.TryExtractDimensions(ms);
            if (dimensions.Width != BannerWidth || dimensions.Height != BannerHeight)
            {
                _statusMessage = $"Banner must be exactly {BannerWidth}x{BannerHeight}.";
                _statusIsError = true;
                return;
            }

            var extension = Path.GetExtension(filePath);
            if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                _statusMessage = "Banner image must be a PNG file.";
                _statusIsError = true;
                return;
            }

            var tempPath = Path.Combine(_fileCacheManager.CacheFolder, $"venue-ad-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(tempPath, fileBytes).ConfigureAwait(false);
            var hash = await Crypto.GetFileHashAsync(tempPath).ConfigureAwait(false);
            var destinationPath = Path.Combine(_fileCacheManager.CacheFolder, $"{hash}.png");

            if (!string.Equals(tempPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(destinationPath))
                {
                    File.Delete(tempPath);
                }
                else
                {
                    File.Move(tempPath, destinationPath);
                }
            }

            _fileCacheManager.CreateCacheEntry(destinationPath, hash);
            _bannerHash = hash;
            _bannerWidth = BannerWidth;
            _bannerHeight = BannerHeight;
            _bannerNeedsUpload = true;
            _statusMessage = "Banner loaded. Remember to save the ad.";
            _statusIsError = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load banner image");
            _statusMessage = "Failed to load banner image.";
            _statusIsError = true;
        }
    }

    private void ClearBanner()
    {
        _bannerHash = null;
        _bannerWidth = null;
        _bannerHeight = null;
        _bannerNeedsUpload = false;
    }

    private async Task RefreshBrowseAsync()
    {
        if (_isLoadingBrowse)
            return;

        _isLoadingBrowse = true;
        try
        {
            var response = await _apiController.VenueRegistryList(new VenueRegistryListRequestDto(0, 50)
            {
                IncludeAds = true,
                IncludeUnlisted = false
            }).ConfigureAwait(false);
            _browseVenues.Clear();
            _browseVenues.AddRange(response.Registries.Where(v => v.Advertisements.Any(ad => ad.IsActive ?? true)));
            
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load venue ads");
            _statusMessage = "Failed to load venue ads.";
            _statusIsError = true;
        }
        finally
        {
            _isLoadingBrowse = false;
        }
    }

    private async Task RefreshOwnedAsync()
    {
        if (_isLoadingOwned)
            return;

        _isLoadingOwned = true;
        try
        {
            var response = await _apiController.VenueRegistryListOwned(new VenueRegistryListOwnedRequestDto(0, 50)
            {
                IncludeAds = true,
                IncludeUnlisted = true
            }).ConfigureAwait(false);
            _ownedVenues.Clear();
            _ownedVenues.AddRange(response.Registries);
            if (_selectedOwnedVenueIndex < 0 && _ownedVenues.Count > 0)
            {
                SelectOwnedVenue(0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load owned venues");
            _statusMessage = "Failed to load owned venues.";
            _statusIsError = true;
        }
        finally
        {
            _isLoadingOwned = false;
        }
    }

    private async Task SaveAdAsync()
    {
        if (_selectedOwnedVenueIndex < 0 || _selectedOwnedVenueIndex >= _ownedVenues.Count)
            return;

        var validation = ValidateAd();
        if (!string.IsNullOrWhiteSpace(validation))
        {
            _statusMessage = validation;
            _statusIsError = true;
            return;
        }

        _isSaving = true;
        _statusMessage = null;
        _statusIsError = false;

        try
        {
            if (_bannerNeedsUpload && !string.IsNullOrWhiteSpace(_bannerHash))
            {
                var progress = new Progress<string>(msg =>
                {
                    _statusMessage = msg;
                    _statusIsError = false;
                });
                var missing = await _fileUploadManager.UploadFiles([_bannerHash], progress).ConfigureAwait(false);
                if (missing.Count > 0)
                {
                    _statusMessage = "Banner upload failed.";
                    _statusIsError = true;
                    return;
                }
                _bannerNeedsUpload = false;
            }

            var venue = _ownedVenues[_selectedOwnedVenueIndex];
            var request = new VenueAdvertisementUpsertRequestDto(venue.Id, _selectedAdId)
            {
                Text = string.IsNullOrWhiteSpace(_adText) ? null : _adText.Trim(),
                BannerFileHash = _bannerHash,
                BannerWidth = _bannerWidth,
                BannerHeight = _bannerHeight,
                IsActive = _adIsActive
            };

            var response = await _apiController.VenueAdvertisementUpsert(request).ConfigureAwait(false);
            if (!response.Success)
            {
                _statusMessage = response.ErrorMessage ?? "Failed to save ad.";
                _statusIsError = true;
                return;
            }

            _statusMessage = response.WasUpdate ? "Ad updated successfully." : "Ad created successfully.";
            _statusIsError = false;

            await RefreshOwnedAsync().ConfigureAwait(false);
            if (response.Advertisement != null)
            {
                _selectedAdId = response.Advertisement.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save venue ad");
            _statusMessage = "Failed to save venue ad.";
            _statusIsError = true;
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task DeleteAdAsync()
    {
        if (_selectedOwnedVenueIndex < 0 || _selectedOwnedVenueIndex >= _ownedVenues.Count || !_selectedAdId.HasValue)
            return;

        _isSaving = true;
        _statusMessage = null;
        _statusIsError = false;
        try
        {
            var venue = _ownedVenues[_selectedOwnedVenueIndex];
            var request = new VenueAdvertisementDeleteRequestDto(venue.Id, _selectedAdId.Value);
            var response = await _apiController.VenueAdvertisementDelete(request).ConfigureAwait(false);
            if (!response.Success)
            {
                _statusMessage = response.ErrorMessage ?? "Failed to delete ad.";
                _statusIsError = true;
                return;
            }

            _statusMessage = "Ad deleted.";
            _statusIsError = false;
            await RefreshOwnedAsync().ConfigureAwait(false);
            ResetAdEditor();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete venue ad");
            _statusMessage = "Failed to delete venue ad.";
            _statusIsError = true;
        }
        finally
        {
            _isSaving = false;
        }
    }
}
