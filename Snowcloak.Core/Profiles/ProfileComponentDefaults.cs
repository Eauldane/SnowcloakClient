using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.Core.Profiles;

public static class ProfileComponentDefaults
{
    public static ProfileBuilderDto CreateTemplate(CharacterProfileDocumentDto document, int template = 0)
    {
        var builder = ProfileTemplates.Create(document, template);
        void Square(IEnumerable<ProfileBlockDto> nodes)
        {
            foreach (var node in nodes)
            {
                node.Rounding = 0;
                Square(node.Children);
            }
        }
        foreach (var layout in ProfileBuilderPolicy.Layouts(builder)) Square(layout);
        return builder;
    }
}
