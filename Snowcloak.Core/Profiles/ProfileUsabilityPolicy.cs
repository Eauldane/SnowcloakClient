using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.Core.Profiles;

public record ProfileSearchHit(string? PageId, string? BlockId, string SectionKey, string Label, string Text);
public record ProfileVisibilityInventory(ProfileAudience Audience, int Pages, int Fields, int Preferences, int Images, List<string> Hidden);

public static class ProfileUsabilityPolicy
{
    public static IEnumerable<ProfileSearchHit> Index(CharacterProfileDocumentDto document)
    {
        var summary = string.Join(" ", new[] { document.CharacterName, document.Title, document.Pronouns, document.Tagline, document.RpStatus, document.Approachability }
            .Concat(document.AtAGlance).Concat(document.Tags.Select(tag => tag.Value)));
        if (!string.IsNullOrWhiteSpace(summary)) yield return new(null, null, "summary", "Character summary", summary);
        var builder = document.Builder;
        var locations = builder?.Version == 2 ? builder.Pages.Select(page => (id: (string?)page.Id, layout: page.Layout))
            : new[] { (id: (string?)null, layout: builder?.Layout ?? []) };
        var blocks = locations.SelectMany(page => Walk(page.layout).Select(block => (page.id, block))).ToList();
        ProfileSearchHit Canonical(ProfileBlockKind kind, string key, string title, string text)
        { var location = blocks.FirstOrDefault(pair => pair.block.Kind == kind); return new(location.id, location.block?.Id, key, title, text); }
        if (!string.IsNullOrWhiteSpace(document.Overview)) yield return Canonical(ProfileBlockKind.Overview, "overview", "Overview", document.Overview);
        if (!string.IsNullOrWhiteSpace(document.OocNotes)) yield return Canonical(ProfileBlockKind.OocNotes, "ooc", "OOC notes", document.OocNotes);
        if (document.ContentRating == ProfileContentRating.Adult && !string.IsNullOrWhiteSpace(document.AdultPreferences))
            yield return Canonical(ProfileBlockKind.AdultPreferences, "adult", "Adult preferences", document.AdultPreferences);
        foreach (var hook in document.Hooks) yield return Canonical(ProfileBlockKind.Hooks, "hooks", hook.Title, hook.Description);
        if (builder == null) yield break;
        foreach (var field in builder.Fields.Where(field => !string.IsNullOrWhiteSpace(field.Value)))
        { var location = blocks.FirstOrDefault(pair => pair.block.Kind == ProfileBlockKind.Fields && pair.block.References.Contains(field.Id));
            yield return new(location.id, location.block?.Id, "fields:" + field.Group, field.Label, field.Group + " " + field.Value); }
        foreach (var preference in builder.Preferences.Where(preference => ProfileBuilderPolicy.IsDirectional(preference)
            ? preference.Doing != ProfilePreferenceRating.Unspecified || preference.Receiving != ProfilePreferenceRating.Unspecified
            : preference.Rating != ProfilePreferenceRating.Unspecified))
        { var catalogue = preference.CatalogueId == null ? null : ProfilePreferenceCatalogue.ById.GetValueOrDefault(preference.CatalogueId);
            var location = blocks.FirstOrDefault(pair => pair.block.Kind == ProfileBlockKind.Preferences);
            yield return new(location.id, location.block?.Id, "preferences", string.IsNullOrWhiteSpace(preference.Name) ? catalogue?.Name ?? "Custom interest" : preference.Name,
                string.Join(" ", catalogue?.Aliases ?? []) + " " + preference.Note + " " + (ProfileBuilderPolicy.IsDirectional(preference)
                    ? "Doing " + preference.Doing + " Receiving " + preference.Receiving : preference.Rating.ToString())); }
        foreach (var location in blocks)
        {
            var block = location.block;
            if (block.Kind is ProfileBlockKind.Heading or ProfileBlockKind.RichText)
                yield return new(location.id, block.Id, "block:" + block.Id, string.IsNullOrWhiteSpace(block.Title) ? "Text" : block.Title, block.Text);
            if (block.Kind is ProfileBlockKind.Image or ProfileBlockKind.Gallery)
                foreach (var asset in builder.Assets.Where(asset => block.References.Contains(asset.Hash) && !string.IsNullOrWhiteSpace(asset.Caption)))
                    yield return new(location.id, block.Id, "block:" + block.Id, "Image caption", asset.Caption);
        }
    }

    public static IEnumerable<ProfileSearchHit> Find(CharacterProfileDocumentDto permittedDocument, string query)
        => string.IsNullOrWhiteSpace(query) ? [] : Index(permittedDocument).Where(hit =>
            (hit.Label + " " + hit.Text).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).Take(50);

    public static ProfileVisibilityInventory Visibility(CharacterProfileDocumentDto source, ProfileAudience audience)
    {
        var projection = ProfileEditSession.BuildPreviewDocument(source, audience);
        var original = source.Builder; var visible = projection.Builder; var hidden = new List<string>();
        if (!string.IsNullOrWhiteSpace(source.Overview) && string.IsNullOrWhiteSpace(projection.Overview)) hidden.Add("Overview");
        if (!string.IsNullOrWhiteSpace(source.OocNotes) && string.IsNullOrWhiteSpace(projection.OocNotes)) hidden.Add("OOC notes");
        if (!string.IsNullOrWhiteSpace(source.AdultPreferences) && string.IsNullOrWhiteSpace(projection.AdultPreferences)) hidden.Add("Adult preferences");
        if (source.Hooks.Count > projection.Hooks.Count) hidden.Add("RP hooks");
        if (source.Boundaries != null && projection.Boundaries == null) hidden.Add("Scene boundaries");
        if (original != null && visible != null)
        {
            var ids = ProfileBuilderPolicy.Layouts(visible).SelectMany(Walk).Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var page in original.Pages.Where(page => !visible.Pages.Any(candidate => candidate.Id == page.Id))) hidden.Add("Page: " + page.Title);
            foreach (var block in ProfileBuilderPolicy.Layouts(original).SelectMany(Walk).Where(block => !ids.Contains(block.Id)))
                hidden.Add("Element: " + (string.IsNullOrWhiteSpace(block.Title) ? block.Kind.ToString() : block.Title));
            foreach (var field in original.Fields.Where(field => !string.IsNullOrWhiteSpace(field.Value) && !visible.Fields.Any(candidate => candidate.Id == field.Id))) hidden.Add("Field: " + field.Label);
            if (original.Preferences.Count > visible.Preferences.Count) hidden.Add("Interests");
            if (ProfileBuilderPolicy.AssetHashes(original).Except(ProfileBuilderPolicy.AssetHashes(visible), StringComparer.Ordinal).Any()) hidden.Add("Profile images");
        }
        return new(audience, visible?.Version == 2 ? visible.Pages.Count : 1, visible?.Fields.Count ?? 0, visible?.Preferences.Count ?? 0,
            visible == null ? 0 : ProfileBuilderPolicy.AssetHashes(visible).Count(), hidden);
    }

    public static IEnumerable<ProfileBlockDto> Walk(IEnumerable<ProfileBlockDto> nodes)
    { foreach (var node in nodes) { yield return node; foreach (var child in Walk(node.Children)) yield return child; } }
}
