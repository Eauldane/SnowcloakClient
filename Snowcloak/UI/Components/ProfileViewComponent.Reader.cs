using Dalamud.Bindings.ImGui;
using Snowcloak.API.Dto.User;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public sealed partial class ProfileViewComponent
{
    private readonly Dictionary<string, bool> _traditionalSections = new(StringComparer.Ordinal);
    private bool _traditionalDefaultOpen = true;
    private string _readerProfile = string.Empty;
    private readonly ProfileReaderViewState _readerView = new();
    public bool UsesClassicView(ProfileBuilderDto? builder)
        => _readerView.UsesClassic(builder, _configService.Current.ProfilesTraditionalOnly);
    private ProfileSearchHit? _readerSearchTarget;
    private bool _readerActive;
    private bool ReaderHighContrast => _readerActive && _configService.Current.ProfileReaderHighContrast;
    private bool ReaderReduceImages => _readerActive && _configService.Current.ProfileReaderReduceImages;
    private float ReaderScale => !_readerActive ? 1 : float.IsFinite(_configService.Current.ProfileReaderTextScale)
        ? Math.Clamp(_configService.Current.ProfileReaderTextScale, .8f, 1.8f) : 1;

    public void ResetReaderView()
    {
        _readerView.Reset(); _readerProfile = string.Empty;
        _comparePreferences = false; _comparisonFilter = 0; _readerSearchTarget = null;
        _galleryImageSelections.Clear();
    }

    private void DrawViewerToolbar(ProfileViewRequest request)
    {
        _readerView.Open(request.IdPrefix);
        using var toolbar = Dalamud.Interface.Utility.Raii.ImRaii.Table("profile-view-toolbar", 2, ImGuiTableFlags.SizingFixedFit);
        if (!toolbar) return;
        ImGui.TableSetupColumn("View", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Safety", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableNextRow(); ImGui.TableNextColumn();
        if (ProfileReaderViewState.CanSwitch(request.Profile.Document.Builder, _configService.Current.ProfilesTraditionalOnly))
        {
            var classic = _readerView.UsesClassic(request.Profile.Document.Builder, _configService.Current.ProfilesTraditionalOnly);
            if (ImGui.Button(classic ? "Custom view" : "Classic view"))
            {
                _readerView.Toggle(request.Profile.Document.Builder, _configService.Current.ProfilesTraditionalOnly);
                _comparePreferences = false; _comparisonFilter = 0;
                if (!classic) DisposeWebRenderer();
            }
        }
        ImGui.TableNextColumn(); request.DrawReportButton?.Invoke();
    }

    private void DrawReaderControls(ProfileViewRequest request)
    {
        if (_readerProfile != request.IdPrefix)
        {
            _readerProfile = request.IdPrefix; _readerSearchTarget = null;
            _traditionalSections.Clear(); _traditionalDefaultOpen = true;
        }
        if (_readerView.UsesClassic(request.Profile.Document.Builder, _configService.Current.ProfilesTraditionalOnly))
        {
            if (ImGui.Button("Expand all sections")) { _traditionalDefaultOpen = true; foreach (var key in _traditionalSections.Keys.ToArray()) _traditionalSections[key] = true; }
            ImGui.SameLine();
            if (ImGui.Button("Collapse all sections")) { _traditionalDefaultOpen = false; foreach (var key in _traditionalSections.Keys.ToArray()) _traditionalSections[key] = false; }
        }
        if (_readerSearchTarget?.SectionKey == "summary") { ImGui.SetScrollY(0); _readerSearchTarget = null; }
    }

    private bool OpenTraditionalSection(string key, string title)
    {
        if (_readerSearchTarget?.SectionKey == key)
        { _traditionalSections[key] = true; ImGui.SetScrollHereY(.15f); _readerSearchTarget = null; }
        ImGui.SetNextItemOpen(_traditionalSections.GetValueOrDefault(key, _traditionalDefaultOpen), ImGuiCond.Always);
        var open = ImGui.CollapsingHeader(title + "###traditional-section:" + key);
        _traditionalSections[key] = open;
        return open;
    }

    private void DrawTraditionalText(string key, string title, string text)
    {
        if (!string.IsNullOrWhiteSpace(text) && OpenTraditionalSection(key, title)) DrawBbCodeSection("", text);
    }
}
