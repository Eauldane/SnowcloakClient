using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.Core.Profiles;

public static class ProfilePresentationPolicy
{
    public static void PlaceNewTraditionalField(ProfileBuilderDto builder, ProfileFieldDto field)
    {
        if (builder.Presentation != ProfilePresentation.Traditional) return;
        var page = builder.Version == 2 ? builder.Pages.OrderBy(page => page.Audience).First() : null;
        var layout = page?.Layout ?? builder.Layout;
        var audience = page?.Audience ?? Snowcloak.API.Data.Enum.ProfileAudience.Public;
        var fields = layout.FirstOrDefault(block => block.Kind == ProfileBlockKind.Fields && block.Audience == audience);
        if (fields == null)
        {
            fields = new() { Kind = ProfileBlockKind.Fields, Title = "Character fields", Audience = audience, Rounding = 0 };
            layout.Add(fields);
        }
        fields.References.Add(field.Id);
    }

    public static bool UsesTraditional(ProfileBuilderDto? builder, bool readerTraditionalOnly = false)
        => readerTraditionalOnly || builder == null || builder.Presentation == ProfilePresentation.Traditional;

    public static ProfileBuilderDto Change(CharacterProfileDocumentDto document, ProfilePresentation presentation)
    {
        if (!Enum.IsDefined(presentation)) throw new ArgumentException("Invalid profile presentation.", nameof(presentation));
        var builder = document.Builder == null ? ProfileComponentDefaults.CreateTemplate(document) : ProfileBuilderPolicy.Clone(document.Builder);
        builder.Presentation = presentation;
        return builder;
    }
}
