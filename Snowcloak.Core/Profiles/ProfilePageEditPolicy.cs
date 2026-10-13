using System.Text.Json;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.Core.Profiles;

public static class ProfilePageEditPolicy
{
    public static ProfileBuilderDto Insert(ProfileBuilderDto source, string? parentId, int index, ProfileBlockDto element, string? pageId = null)
        => Edit(source, page => Destination(page, parentId, pageId).Insert(Math.Clamp(index, 0, Destination(page, parentId, pageId).Count),
            JsonSerializer.Deserialize<ProfileBlockDto>(JsonSerializer.Serialize(element))!));

    public static ProfileBuilderDto Move(ProfileBuilderDto source, string elementId, string? parentId, int index, string? pageId = null)
        => Edit(source, page =>
        {
            var origin = Locate(ProfileBuilderPolicy.LayoutFor(page, pageId), elementId) ?? throw new ArgumentException("Element no longer exists.");
            var element = origin.Nodes[origin.Index];
            if (parentId == elementId || (parentId != null && Locate(element.Children, parentId) != null))
                throw new ArgumentException("An element cannot be moved inside itself.");
            var destination = Destination(page, parentId, pageId);
            if (ReferenceEquals(origin.Nodes, destination) && origin.Index < index) index--;
            origin.Nodes.RemoveAt(origin.Index);
            destination.Insert(Math.Clamp(index, 0, destination.Count), element);
        });

    public static ProfileBuilderDto Delete(ProfileBuilderDto source, string elementId, string? pageId = null)
        => Edit(source, page =>
        {
            var origin = Locate(ProfileBuilderPolicy.LayoutFor(page, pageId), elementId) ?? throw new ArgumentException("Element no longer exists.");
            origin.Nodes.RemoveAt(origin.Index);
        });

    public static ProfileBuilderDto Duplicate(ProfileBuilderDto source, string elementId, string? pageId = null)
        => Edit(source, page =>
        {
            var origin = Locate(ProfileBuilderPolicy.LayoutFor(page, pageId), elementId) ?? throw new ArgumentException("Element no longer exists.");
            var copy = JsonSerializer.Deserialize<ProfileBlockDto>(JsonSerializer.Serialize(origin.Nodes[origin.Index]))!;
            void Renew(ProfileBlockDto node) { node.Id = Guid.NewGuid().ToString("N"); foreach (var child in node.Children) Renew(child); }
            Renew(copy); origin.Nodes.Insert(origin.Index + 1, copy);
        });

    public static (List<ProfileBlockDto> Nodes, int Index)? Locate(List<ProfileBlockDto> nodes, string id)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index].Id == id) return (nodes, index);
            var nested = Locate(nodes[index].Children, id); if (nested != null) return nested;
        }
        return null;
    }

    private static List<ProfileBlockDto> Destination(ProfileBuilderDto page, string? parentId, string? pageId)
    {
        if (parentId == null) return ProfileBuilderPolicy.LayoutFor(page, pageId);
        var location = Locate(ProfileBuilderPolicy.LayoutFor(page, pageId), parentId) ?? throw new ArgumentException("Section no longer exists.");
        var parent = location.Nodes[location.Index];
        if (!ProfileBuilderPolicy.IsContainer(parent.Kind)) throw new ArgumentException("Only sections, columns and tabs can contain elements.");
        return parent.Children;
    }

    private static ProfileBuilderDto Edit(ProfileBuilderDto source, Action<ProfileBuilderDto> action)
    {
        var next = ProfileBuilderPolicy.Clone(source); action(next);
        ProfileBuilderPolicy.Validate(next, allowIncomplete: true); return next;
    }
}
