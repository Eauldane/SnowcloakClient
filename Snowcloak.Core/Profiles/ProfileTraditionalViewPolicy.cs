using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.Core.Profiles;

public static class ProfileTraditionalViewPolicy
{
    public static IEnumerable<ProfileBlockDto> Content(ProfileBuilderDto builder)
    {
        foreach (var layout in ProfileBuilderPolicy.Layouts(builder))
            foreach (var block in Flatten(layout)) yield return block;
    }

    private static IEnumerable<ProfileBlockDto> Flatten(IEnumerable<ProfileBlockDto> blocks)
    {
        foreach (var block in blocks)
        {
            if (ProfileBuilderPolicy.IsContainer(block.Kind))
            {
                foreach (var child in Flatten(block.Children)) yield return child;
            }
            else if (block.Kind is ProfileBlockKind.Heading or ProfileBlockKind.RichText or
                ProfileBlockKind.Image or ProfileBlockKind.Gallery or ProfileBlockKind.Divider ||
                block.Kind == ProfileBlockKind.Link && block.PageTargetId == null)
                yield return block;
        }
    }
}
