using System.Text.Json;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.Core.Profiles;

public static class ProfileDocumentPagePolicy
{
    public static void Apply(ProfileBuilderDto builder, JsonElement message, string? activePageId)
    {
        if (builder.Version == 1)
        {
            builder.Pages = [new() { Id = "home", Title = "Home", Audience = ProfileAudience.Public, Layout = builder.Layout }];
            builder.Layout = []; builder.StartPageId = "home"; builder.Version = 2;
            builder.LayoutMode = ProfileLayoutMode.Traditional;
        }
        var operation = message.GetProperty("type").GetString();
        string Text(string key) => message.GetProperty(key).GetString() ?? throw new ArgumentException("A page identifier is required.");
        ProfilePageDto Page() => builder.Pages.FirstOrDefault(p => p.Id == Text("id")) ?? throw new ArgumentException("That page no longer exists.");
        switch (operation)
        {
            case "pageAdd": builder.Pages.Add(new() { Title = "New page", Audience = ProfileAudience.Public }); break;
            case "pageRename": Page().Title = Text("value"); break;
            case "pageAudience": Page().Audience = (ProfileAudience)message.GetProperty("value").GetInt32(); break;
            case "pageDefault": builder.StartPageId = Page().Id; break;
            case "pageMove":
                var moving = Page(); builder.Pages.Remove(moving);
                builder.Pages.Insert(Math.Clamp(message.GetProperty("index").GetInt32(), 0, builder.Pages.Count), moving); break;
            case "pageDuplicate":
                var source = Page(); var copy = JsonSerializer.Deserialize<ProfilePageDto>(JsonSerializer.Serialize(source))!;
                copy.Id = Guid.NewGuid().ToString("N"); copy.Title = (source.Title.Length > 73 ? source.Title[..73] : source.Title) + " (copy)";
                void Renew(ProfileBlockDto node) { node.Id = Guid.NewGuid().ToString("N"); foreach (var child in node.Children) Renew(child); }
                foreach (var copiedNode in copy.Layout) Renew(copiedNode);
                void Retarget(ProfileBlockDto block) { if (block.PageTargetId == source.Id) block.PageTargetId = copy.Id; foreach (var child in block.Children) Retarget(child); }
                foreach (var copiedNode in copy.Layout) Retarget(copiedNode);
                builder.Pages.Insert(builder.Pages.IndexOf(source) + 1, copy); break;
            case "pageDelete":
                if (builder.Pages.Count == 1) throw new ArgumentException("Keep at least one profile page.");
                var deleted = Page(); builder.Pages.Remove(deleted);
                void RemoveLinks(List<ProfileBlockDto> blocks)
                {
                    blocks.RemoveAll(b => b.PageTargetId == deleted.Id);
                    foreach (var block in blocks) RemoveLinks(block.Children);
                }
                foreach (var remaining in builder.Pages) RemoveLinks(remaining.Layout);
                if (builder.StartPageId == deleted.Id) builder.StartPageId = builder.Pages[0].Id; break;
            case "moveToPage":
                var from = ProfileBuilderPolicy.LayoutFor(builder, activePageId);
                var origin = ProfilePageEditPolicy.Locate(from, Text("id")) ?? throw new ArgumentException("That element no longer exists.");
                var destination = ProfileBuilderPolicy.LayoutFor(builder, Text("destinationPageId"));
                var node = origin.Nodes[origin.Index]; origin.Nodes.RemoveAt(origin.Index); destination.Add(node); break;
            default: throw new ArgumentException("Unsupported page operation.");
        }
    }
}
