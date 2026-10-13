using System.Numerics;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.Core.Profiles;

public record ProfilePageRect(float X, float Y, float Width, float Height)
{
    public bool Contains(Vector2 point) => point.X >= X && point.Y >= Y && point.X <= X + Width && point.Y <= Y + Height;
}

public record ProfilePagePlacement(ProfileBlockDto Element, string? ParentId, int Index,
    ProfilePageRect Bounds, ProfilePageRect ContentBounds);

public static class ProfilePageLayout
{
    public static (List<ProfilePagePlacement> Elements, float Height) Arrange(ProfileBuilderDto page, float width,
        float scale, Func<ProfileBlockDto, float, float> measureContent)
    {
        if (!float.IsFinite(width) || width < 1 || !float.IsFinite(scale) || scale <= 0)
            throw new ArgumentException("Invalid page viewport.");
        var result = new List<ProfilePagePlacement>();
        float Place(ProfileBlockDto element, string? parent, int index, float x, float y, float available)
        {
            var inset = Math.Min(element.Padding * scale, Math.Max(0, (available - 20 * scale) / 2));
            var contentX = x + inset; var contentY = y + inset;
            var contentWidth = Math.Max(1, available - inset * 2);
            var slot = result.Count;
            result.Add(new(element, parent, index, new(x, y, available, 0), new(contentX, contentY, contentWidth, 0)));
            var heading = measureContent(element, contentWidth);
            var height = heading;
            if (ProfileBuilderPolicy.IsContainer(element.Kind))
            {
                var childrenY = contentY + heading;
                var gap = element.Spacing * scale;
                if (element.Kind == ProfileBlockKind.Columns && element.Children.Count > 1
                    && contentWidth >= element.Children.Count * 180 * scale + gap * (element.Children.Count - 1))
                {
                    var cellWidth = Math.Max(1, (contentWidth - gap * (element.Children.Count - 1)) / element.Children.Count);
                    var tallest = 0f;
                    for (var child = 0; child < element.Children.Count; child++)
                        tallest = Math.Max(tallest, Place(element.Children[child], element.Id, child,
                            contentX + child * (cellWidth + gap), childrenY, cellWidth));
                    height += tallest;
                }
                else
                {
                    for (var child = 0; child < element.Children.Count; child++)
                    {
                        var childHeight = Place(element.Children[child], element.Id, child, contentX, childrenY, contentWidth);
                        childrenY += childHeight + gap; height += childHeight + gap;
                    }
                }
            }
            height = Math.Max(32 * scale, height) + inset * 2;
            result[slot] = new(element, parent, index, new(x, y, available, height), new(contentX, contentY, contentWidth, height - inset * 2));
            return height;
        }
        var cursor = 0f;
        for (var index = 0; index < page.Layout.Count; index++)
            cursor += Place(page.Layout[index], null, index, 0, cursor, width) + 12 * scale;
        return (result, Math.Max(cursor, 160 * scale));
    }

    public static (string? ParentId, int Index, ProfilePageRect Marker) DropTarget(
        ProfileBuilderDto page, IReadOnlyList<ProfilePagePlacement> placements, Vector2 point, float width, string? draggedId)
    {
        var dragged = draggedId == null ? null : ProfilePageEditPolicy.Locate(page.Layout, draggedId);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        void Exclude(ProfileBlockDto node) { excluded.Add(node.Id); foreach (var child in node.Children) Exclude(child); }
        if (dragged != null) Exclude(dragged.Value.Nodes[dragged.Value.Index]);
        var container = placements.LastOrDefault(p => p.Element.Kind == ProfileBlockKind.Flow
            && p.ContentBounds.Contains(point) && !excluded.Contains(p.Element.Id));
        var parentId = container?.Element.Id;
        var children = placements.Where(p => p.ParentId == parentId && p.Element.Id != draggedId).OrderBy(p => p.Index).ToList();
        var next = children.FirstOrDefault(p => point.Y < p.Bounds.Y + p.Bounds.Height / 2);
        var count = parentId == null ? page.Layout.Count : container!.Element.Children.Count;
        var index = next?.Index ?? count;
        var markerY = next?.Bounds.Y ?? (children.Count == 0 ? container?.ContentBounds.Y ?? 0 : children[^1].Bounds.Y + children[^1].Bounds.Height);
        return (parentId, index, new(container?.ContentBounds.X ?? 0, markerY, container?.ContentBounds.Width ?? width, 2));
    }
}
