using System.Text.Json;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.Core.Profiles;

public record ProfileSavedSection(string Id, string Name, ProfileBlockDto Root,
    List<ProfileFieldDto> Fields, List<ProfileAssetDto> Assets, List<ProfilePreferenceDto> Preferences,
    ProfileAudience PreferenceAudience = ProfileAudience.Paired);

public static class ProfileSectionLibrary
{
    public const int MaxSections = 32;
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public static ProfileSavedSection Capture(CharacterProfileDocumentDto document, string blockId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("Name the section (up to 80 characters).");
        var builder = document.Builder ?? throw new ArgumentException("Create a profile section first.");
        ProfileBuilderPolicy.Validate(builder, allowIncomplete: true);
        var root = Copy(ProfileBuilderPolicy.Layouts(builder).SelectMany(ProfileUsabilityPolicy.Walk).SingleOrDefault(block => block.Id == blockId)
            ?? throw new ArgumentException("Choose an existing element to save."));
        var nodes = ProfileUsabilityPolicy.Walk([root]).ToList();
        foreach (var node in nodes)
            if (node.Kind is ProfileBlockKind.Overview or ProfileBlockKind.OocNotes)
            {
                var visibility = document.FieldVisibility ?? new();
                var audience = node.Kind == ProfileBlockKind.Overview ? visibility.Overview : visibility.OocNotes;
                if (node.Audience < audience) node.Audience = audience;
                node.Text = node.Kind == ProfileBlockKind.Overview ? document.Overview : document.OocNotes;
                node.Kind = ProfileBlockKind.RichText;
            }
        return new(Guid.NewGuid().ToString("N"), name.Trim(), root,
            Copy(builder.Fields.Where(field => nodes.Any(node => node.Kind == ProfileBlockKind.Fields && node.References.Contains(field.Id))).ToList()),
            Copy(builder.Assets.Where(asset => nodes.Any(node => node.Kind is ProfileBlockKind.Image or ProfileBlockKind.Gallery && node.References.Contains(asset.Hash))).ToList()),
            nodes.Any(node => node.Kind == ProfileBlockKind.Preferences) ? Copy(builder.Preferences) : [], builder.PreferenceAudience);
    }

    public static ProfileBuilderDto Insert(ProfileBuilderDto source, ProfileSavedSection saved, string? pageId)
    {
        var builder = ProfileBuilderPolicy.Clone(source); var section = Copy(saved);
        var map = section.Fields.ToDictionary(field => field.Id, _ => Guid.NewGuid().ToString("N"));
        foreach (var field in section.Fields) { field.Id = map[field.Id]; builder.Fields.Add(field); }
        foreach (var asset in section.Assets) if (!builder.Assets.Any(existing => existing.Hash == asset.Hash)) builder.Assets.Add(asset);
        foreach (var preference in section.Preferences)
            if (!builder.Preferences.Any(existing => preference.CatalogueId != null && existing.CatalogueId == preference.CatalogueId))
            { preference.Id = Guid.NewGuid().ToString("N"); builder.Preferences.Add(preference); }
        if (section.Preferences.Count > 0 && builder.PreferenceAudience < section.PreferenceAudience)
            builder.PreferenceAudience = section.PreferenceAudience;
        var audience = builder.Version == 2 ? builder.Pages.Single(page => page.Id == pageId).Audience : section.Root.Audience;
        foreach (var node in ProfileUsabilityPolicy.Walk([section.Root]))
        {
            node.Id = Guid.NewGuid().ToString("N");
            if (node.Audience < audience) node.Audience = audience;
            if (node.Kind == ProfileBlockKind.Fields) node.References = node.References.Select(id => map.GetValueOrDefault(id, id)).ToList();
            if (node.PageTargetId != null) { node.PageTargetId = null; node.Text = string.Empty; }
        }
        var layout = ProfileBuilderPolicy.LayoutFor(builder, pageId); layout.Add(section.Root);
        ProfileBuilderPolicy.Validate(builder, allowIncomplete: true);
        return builder;
    }
}
