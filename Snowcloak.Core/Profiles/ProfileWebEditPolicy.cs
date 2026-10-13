using System.Text.Json;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.Core.Profiles;

public static class ProfileWebEditPolicy
{
    public const int MaxMessageBytes = 65536;

    public static CharacterProfileDocumentDto Apply(ProfileEditSession session, JsonElement message)
    {
        if (message.GetProperty("ident").GetString() != session.LoadedIdent ||
            message.GetProperty("revision").GetInt64() != session.LoadedRevision)
            throw new ArgumentException("The editor character or revision changed. Refresh the page before editing.");
        var document = session.ToDocument();
        var page = document.Builder ?? throw new ArgumentException("No profile page is loaded.");
        var kind = message.GetProperty("type").GetString();
        var pageId = message.TryGetProperty("pageId", out var activePage) ? activePage.GetString() : null;
        List<ProfileBlockDto> Layout() => ProfileBuilderPolicy.LayoutFor(page, pageId);
        string Text(string key) => message.GetProperty(key).GetString() ?? throw new ArgumentException("A text value is required.");
        ProfileBlockDto Element() => ProfilePageEditPolicy.Locate(Layout(), Text("id")) is { } location
            ? location.Nodes[location.Index] : throw new ArgumentException("That element no longer exists.");
        switch (kind)
        {
            case "pageAdd": case "pageRename": case "pageAudience": case "pageMove": case "pageDuplicate": case "pageDelete": case "pageDefault": case "moveToPage":
                ProfileDocumentPagePolicy.Apply(page, message, pageId); break;
            case "element":
                var node = Element(); var value = message.GetProperty("value");
                switch (Text("property"))
                {
                    case "title": node.Title = value.GetString()!; break;
                    case "text": node.Text = value.GetString()!; break;
                    case "audience": node.Audience = (ProfileAudience)value.GetInt32(); break;
                    case "background": node.Background = value.GetString()!; break;
                    case "foreground": node.Foreground = value.GetString()!; break;
                    case "padding": node.Padding = value.GetSingle(); break;
                    case "spacing": node.Spacing = value.GetSingle(); break;
                    case "rounding": node.Rounding = value.GetSingle(); break;
                    case "border": node.Border = value.GetSingle(); break;
                    case "galleryLayout": node.GalleryLayout = (ProfileGalleryLayout)value.GetInt32(); break;
                    case "fontScale": node.FontScale = value.GetSingle(); break;
                    case "font": node.Font = (ProfileFont)value.GetInt32(); break;
                    case "useThemeForeground": node.UseThemeForeground = value.GetBoolean(); break;
                    case "references": node.References = value.EnumerateArray().Select(v => v.GetString()!).ToList(); break;
                    case "pageTargetId": node.PageTargetId = value.GetString() is { Length: > 0 } target ? target : null; break;
                    case "relativeWidth": node.RelativeWidth = value.GetSingle(); break;
                    case "textAlignment": node.TextAlignment = (ProfileAlignment)value.GetInt32(); break;
                    case "elementAlignment": node.ElementAlignment = (ProfileAlignment)value.GetInt32(); break;
                    case "marginTop": node.MarginTop = value.GetSingle(); break;
                    case "marginBottom": node.MarginBottom = value.ValueKind == JsonValueKind.Null ? null : value.GetSingle(); break;
                    case "kind":
                        var arrangement = (ProfileBlockKind)value.GetInt32();
                        if (!ProfileBuilderPolicy.IsContainer(node.Kind) || !ProfileBuilderPolicy.IsContainer(arrangement))
                            throw new ArgumentException("Only container arrangements can be changed.");
                        node.Kind = arrangement; break;
                    default: throw new ArgumentException("Unsupported element property.");
                }
                break;
            case "canonical":
                switch (Text("property"))
                {
                    case "overview": document.Overview = Text("value"); break;
                    case "oocNotes": document.OocNotes = Text("value"); break;
                    case "adultPreferences": document.AdultPreferences = Text("value"); break;
                    default: throw new ArgumentException("Unsupported character field.");
                }
                break;
            case "containerCount":
                var container = Element();
                if (container.Kind is not (ProfileBlockKind.Columns or ProfileBlockKind.Tabs))
                    throw new ArgumentException("Select columns or tabs to change their count.");
                var count = message.GetProperty("value").GetInt32();
                var maximum = container.Kind == ProfileBlockKind.Columns ? 3 : ProfileBuilderPolicy.MaxNodes - 1;
                if (count < 1 || count > maximum) throw new ArgumentException($"Choose between 1 and {maximum} sections.");
                if (count < container.Children.Count)
                {
                    if (container.Children.Skip(count).Any(child => child.Children.Count > 0 || child.References.Count > 0 ||
                        !string.IsNullOrWhiteSpace(child.Text) || !ProfileBuilderPolicy.IsContainer(child.Kind)))
                        throw new ArgumentException("Move content out of the trailing columns or tabs before reducing their count. Nothing was removed.");
                    container.Children.RemoveRange(count, container.Children.Count - count);
                }
                while (container.Children.Count < count)
                    container.Children.Add(new() { Kind = ProfileBlockKind.Flow, Rounding = 0, Audience = container.Audience,
                        Title = container.Kind == ProfileBlockKind.Tabs ? $"Tab {container.Children.Count + 1}" : string.Empty });
                break;
            case "theme":
                switch (Text("property"))
                {
                    case "background": page.Theme.Background = Text("value"); break;
                    case "foreground": page.Theme.Foreground = Text("value"); break;
                    case "accent": page.Theme.Accent = Text("value"); break;
                    case "customWindowColours": page.Theme.CustomWindowColours = message.GetProperty("value").GetBoolean(); break;
                    case "windowBackground": page.Theme.WindowBackground = Text("value"); break;
                    case "windowText": page.Theme.WindowText = Text("value"); break;
                    case "windowBorder": page.Theme.WindowBorder = Text("value"); break;
                    case "windowTitle": page.Theme.WindowTitle = Text("value"); break;
                    case "windowButton": page.Theme.WindowButton = Text("value"); break;
                    case "windowAccent": page.Theme.WindowAccent = Text("value"); break;
                    case "windowMutedText": page.Theme.WindowMutedText = Text("value"); break;
                    case "windowSection": page.Theme.WindowSection = Text("value"); break;
                    case "windowCard": page.Theme.WindowCard = Text("value"); break;
                    case "windowTag": page.Theme.WindowTag = Text("value"); break;
                    case "windowBadge": page.Theme.WindowBadge = Text("value"); break;
                    case "customChipColours": page.Theme.CustomChipColours = message.GetProperty("value").GetBoolean(); break;
                    case "backgroundFit": page.Theme.BackgroundFit = (ProfileBackgroundFit)message.GetProperty("value").GetInt32(); break;
                    case "backgroundPosition": page.Theme.BackgroundPosition = (ProfileBackgroundPosition)message.GetProperty("value").GetInt32(); break;
                    case "contentWidth": page.Theme.ContentWidth = message.GetProperty("value").GetSingle(); break;
                    case "pagePadding": page.Theme.PagePadding = message.GetProperty("value").GetSingle(); break;
                    case "navigation": page.Theme.Navigation = (ProfileNavigationStyle)message.GetProperty("value").GetInt32(); break;
                    case "textAlignment": page.Theme.TextAlignment = (ProfileAlignment)message.GetProperty("value").GetInt32(); break;
                    case "backgroundHash":
                        var hash = Text("value");
                        if (hash.Length > 0 && hash != page.Theme.BackgroundHash && !page.Assets.Any(a => a.Hash == hash))
                            throw new ArgumentException("Select an image already uploaded to this profile.");
                        page.Theme.BackgroundHash = hash.Length == 0 ? null : hash; break;
                    default: throw new ArgumentException("Unsupported theme property.");
                }
                break;
            case "insert":
                var blockKind = (ProfileBlockKind)message.GetProperty("kind").GetInt32();
                var parent = message.GetProperty("parentId").GetString();
                var owner = parent == null ? null : ProfilePageEditPolicy.Locate(Layout(), parent);
                var audience = owner == null ? (page.Version == 2
                    ? page.Pages.Single(p => p.Id == pageId).Audience : ProfileAudience.Public) : owner.Value.Nodes[owner.Value.Index].Audience;
                var added = new ProfileBlockDto { Kind = blockKind, Rounding = 0, Audience = audience };
                if (blockKind is ProfileBlockKind.Heading or ProfileBlockKind.RichText) added.Text = blockKind == ProfileBlockKind.Heading ? "New heading" : "Write your story here.";
                if (blockKind == ProfileBlockKind.Columns) added.Children = [new() { Rounding = 0, Audience = audience }, new() { Rounding = 0, Audience = audience }];
                if (blockKind == ProfileBlockKind.Tabs) added.Children = [new() { Title = "First tab", Rounding = 0, Audience = audience }];
                if (blockKind == ProfileBlockKind.Fields) added.References = page.Fields.Select(f => f.Id).ToList();
                document.Builder = ProfilePageEditPolicy.Insert(page, parent, message.GetProperty("index").GetInt32(), added, pageId); break;
            case "move": document.Builder = ProfilePageEditPolicy.Move(page, Text("id"), message.GetProperty("parentId").GetString(), message.GetProperty("index").GetInt32(), pageId); break;
            case "delete": document.Builder = ProfilePageEditPolicy.Delete(page, Text("id"), pageId); break;
            case "duplicate": document.Builder = ProfilePageEditPolicy.Duplicate(page, Text("id"), pageId); break;
            case "template":
                var index = message.GetProperty("index").GetInt32();
                if (index < -1 || index > 3) throw new ArgumentException("Unknown profile template.");
                if (index == -1) { Layout().Clear(); page.LayoutMode = ProfileLayoutMode.Traditional; }
                else if (page.Version == 1) document.Builder = ProfileComponentDefaults.CreateTemplate(document, index);
                else
                {
                    var template = ProfileComponentDefaults.CreateTemplate(document with { Builder = page with { Version = 1, Pages = [], StartPageId = null, Layout = Layout() } }, index);
                    page.Fields = template.Fields; Layout().Clear(); Layout().AddRange(template.Layout);
                }
                break;
            case "field":
                var field = page.Fields.FirstOrDefault(f => f.Id == Text("id")) ?? throw new ArgumentException("Unknown field.");
                switch (Text("property"))
                {
                    case "label": field.Label = Text("value"); break;
                    case "group": field.Group = Text("value"); break;
                    case "value": field.Value = Text("value"); break;
                    case "audience": field.Audience = (ProfileAudience)message.GetProperty("value").GetInt32(); break;
                    default: throw new ArgumentException("Unsupported field property.");
                }
                break;
            case "addField":
                var fieldBlock = Element();
                if (fieldBlock.Kind != ProfileBlockKind.Fields) throw new ArgumentException("Select a character fields element.");
                var created = new ProfileFieldDto { Label = "New field", Audience = fieldBlock.Audience };
                page.Fields.Add(created); fieldBlock.References.Add(created.Id); break;
            case "preferenceAudience": page.PreferenceAudience = (ProfileAudience)message.GetProperty("value").GetInt32(); break;
            case "preference":
                var id = Text("id"); var preference = page.Preferences.FirstOrDefault(p => p.Id == id || p.CatalogueId == id);
                if (preference == null)
                {
                    if (!Snowcloak.API.Data.ProfilePreferenceCatalogue.ById.ContainsKey(id)) throw new ArgumentException("Unknown catalogue interest.");
                    preference = new() { CatalogueId = id }; page.Preferences.Add(preference);
                }
                switch (Text("property"))
                {
                    case "rating": preference.Rating = (ProfilePreferenceRating)message.GetProperty("value").GetInt32(); break;
                    case "doing": preference.Doing = (ProfilePreferenceRating)message.GetProperty("value").GetInt32(); break;
                    case "receiving": preference.Receiving = (ProfilePreferenceRating)message.GetProperty("value").GetInt32(); break;
                    case "note": preference.Note = Text("value"); break;
                    case "name": preference.Name = Text("value"); break;
                    case "directional": preference.Directional = message.GetProperty("value").GetBoolean(); break;
                    case "catalogueId": preference.CatalogueId = Text("value") is { Length: > 0 } link ? link : null; break;
                    default: throw new ArgumentException("Unsupported interest property.");
                }
                break;
            case "addPreference": page.Preferences.Add(new() { Name = "Custom interest" }); break;
            case "asset":
                var asset = page.Assets.FirstOrDefault(a => a.Hash == Text("id")) ?? throw new ArgumentException("Unknown profile image.");
                switch (Text("property"))
                {
                    case "caption": asset.Caption = Text("value"); break;
                    case "audience": asset.Audience = (ProfileAudience)message.GetProperty("value").GetInt32(); break;
                    case "focalX": asset.FocalX = message.GetProperty("value").GetSingle(); break;
                    case "focalY": asset.FocalY = message.GetProperty("value").GetSingle(); break;
                    case "fit": asset.Fit = (ProfileImageFit)message.GetProperty("value").GetInt32(); break;
                    case "aspectRatio": asset.AspectRatio = message.GetProperty("value").GetSingle(); break;
                    default: throw new ArgumentException("Unsupported image property.");
                }
                break;
            default: throw new ArgumentException("Unsupported profile operation.");
        }
        ProfileBuilderPolicy.Validate(document.Builder!, allowIncomplete: true);
        var validation = new ProfileEditSession(); validation.Load(session.LoadedIdent, session.LoadedRevision, document);
        if (!validation.Validate().IsValid) throw new ArgumentException("Profile text or item limit exceeded.");
        return document;
    }
}
