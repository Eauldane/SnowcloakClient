using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Dto.User;
using Snowcloak.Core.Profiles;

namespace Snowcloak.UI.Components;

public sealed partial class ProfileViewComponent
{
    private void DrawTraditionalBuilderContent(CharacterProfileDocumentDto document,
        CharacterProfileDocumentDto? mine, string id, ProfileViewRequest request)
    {
        var builder = document.Builder!;
        if (_comparisonProfile != id)
        {
            _comparisonProfile = id; _comparePreferences = false; _comparisonFilter = 0;
        }
        foreach (var group in builder.Fields.Where(field => !string.IsNullOrWhiteSpace(field.Value)).GroupBy(field => field.Group))
        {
            if (!OpenTraditionalSection("fields:" + group.Key, string.IsNullOrWhiteSpace(group.Key) ? "Character fields" : group.Key)) continue;
            foreach (var field in group) DrawBbCodeSection(field.Label, field.Value);
        }
        if (builder.Preferences.Count > 0 && OpenTraditionalSection("preferences", "Interests"))
        {
            ImGui.Separator(); ImGui.TextUnformatted("Interests");
            DrawPreferenceComparisonControls(builder, mine, request);
            DrawPreferences(builder, mine?.Builder);
        }
        foreach (var block in ProfileTraditionalViewPolicy.Content(builder))
        {
            if (!HasContent(block, document)) continue;
            if (!OpenTraditionalSection("block:" + block.Id, string.IsNullOrWhiteSpace(block.Title) ? block.Kind.ToString() : block.Title)) continue;
            using var blockId = ImRaii.PushId(id + ":traditional:" + block.Id);
            ImGui.Spacing();
            DrawBlockContent(block, document, mine, id, request, 0);
        }
    }
}
