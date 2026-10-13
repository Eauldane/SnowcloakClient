using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Profiles;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI;

public sealed partial class EditProfileUi
{
    private string _sectionName = "Reusable section", _sectionSource = string.Empty, _sectionPage = string.Empty;
    private string? _sectionDelete;
    private long _visibilityEpoch = -1;
    private ProfileVisibilityInventory[] _visibilityReports = [];

    private void DrawVisibilityInventory()
    {
        ImGui.TextWrapped("Hidden items are restricted, empty or unplaced. Check Public and Paired previews.");
        if (_visibilityEpoch != _webEditEpoch)
        {
            var document = _session.ToDocument();
            _visibilityReports = new[] { ProfileAudience.Public, ProfileAudience.Paired }.Select(audience => ProfileUsabilityPolicy.Visibility(document, audience)).ToArray();
            _visibilityEpoch = _webEditEpoch;
        }
        foreach (var report in _visibilityReports)
        {
            var audience = report.Audience;
            if (!ImGui.CollapsingHeader($"{audience}: {report.Fields} fields - {report.Preferences} interests - {report.Images} images - {report.Hidden.Count} hidden items")) continue;
            if (ImGui.Button("Preview " + audience)) { _previewMode = audience == ProfileAudience.Public ? 0 : 1; _compactPreview = true; }
            foreach (var item in report.Hidden) ImGui.TextWrapped(item);
        }
    }

    private void SaveReusableSection(string blockId, string? suggestedName = null)
    {
        try
        {
            if (_configService.Current.ProfileSavedSections.Count >= ProfileSectionLibrary.MaxSections)
                throw new ArgumentException("The local library holds up to 32 sections. Remove one before saving another.");
            var saved = ProfileSectionLibrary.Capture(_session.ToDocument(), blockId, suggestedName ?? _sectionName);
            var directory = Path.Combine(_configService.ConfigurationDirectory, "rp-profile-draft-images");
            Directory.CreateDirectory(directory);
            foreach (var asset in saved.Assets)
            {
                var source = Path.Combine(directory, asset.Hash + ".png");
                if (File.Exists(source)) continue;
                if (!_imageTransferService.TryGetImage(asset.Hash, out var bytes) || bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024)
                    throw new ArgumentException("Wait for the section's images to load before saving it. Image sources are required for reuse.");
                File.WriteAllBytes(source, bytes);
            }
            _configService.Update(config => config.ProfileSavedSections.Add(saved));
            SetStatus("Section saved to your local library.");
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { SetStatus(error.Message, true); }
    }

    private void DrawReusableSections()
    {
        ImGui.TextWrapped("Saved locally.");
        if (_session.Builder is not { } builder) return;
        using var disabled = ImRaii.Disabled(IsBusy);
        ImGui.InputText("Section name", ref _sectionName, 80);
        var nodes = ProfileBuilderPolicy.Layouts(builder).SelectMany(ProfileUsabilityPolicy.Walk).ToArray();
        if (ImGui.BeginCombo("Save element", nodes.FirstOrDefault(node => node.Id == _sectionSource)?.Title ?? "Choose element"))
        {
            foreach (var node in nodes)
                if (ImGui.Selectable((string.IsNullOrWhiteSpace(node.Title) ? node.Kind.ToString() : node.Title) + "###save:" + node.Id)) _sectionSource = node.Id;
            ImGui.EndCombo();
        }
        if (ImGui.Button("Save selected section")) SaveReusableSection(_sectionSource);
        if (builder.Version == 2)
        {
            if (!builder.Pages.Any(page => page.Id == _sectionPage)) _sectionPage = builder.StartPageId ?? builder.Pages[0].Id;
            if (ImGui.BeginCombo("Insert into page", builder.Pages.FirstOrDefault(page => page.Id == _sectionPage)?.Title ?? "Choose page"))
            {
                foreach (var page in builder.Pages) if (ImGui.Selectable(page.Title + "###insert:" + page.Id)) _sectionPage = page.Id;
                ImGui.EndCombo();
            }
        }
        foreach (var saved in _configService.Current.ProfileSavedSections.ToArray())
        {
            using var id = ImRaii.PushId(saved.Id);
            var name = saved.Name;
            if (ImGui.InputText("##section-name", ref name, 80) && !string.IsNullOrWhiteSpace(name))
                _configService.Update(config => { var index = config.ProfileSavedSections.FindIndex(section => section.Id == saved.Id); if (index >= 0) config.ProfileSavedSections[index] = saved with { Name = name }; });
            if (ImGui.Button("Insert section"))
            {
                try
                {
                    var inserted = ProfileSectionLibrary.Insert(builder, saved, builder.Version == 2 ? _sectionPage : null);
                    _builderEditor.RememberExternalEdit(_session); _session.Builder = inserted; MarkDirty();
                    SetStatus("Section inserted into your draft. Review visibility before publishing.");
                }
                catch (ArgumentException error) { SetStatus(error.Message, true); }
            }
            ImGui.SameLine(); if (ImGui.Button("Delete from library")) _sectionDelete = saved.Id;
            if (_sectionDelete == saved.Id && ImGui.Button("Confirm delete saved section"))
            { _configService.Update(config => config.ProfileSavedSections.RemoveAll(section => section.Id == saved.Id)); _sectionDelete = null; }
        }
    }
}
