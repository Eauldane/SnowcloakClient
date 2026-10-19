using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Snowcloak.API.Data;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Profiles;

namespace Snowcloak.UI.Components;

public sealed unsafe partial class ProfileBuilderEditorSection
{
    private int _interestCategory;
    private int _interestRating;
    private static readonly string[] InterestCategories = ["All categories", .. ProfilePreferenceCatalogue.Entries.Select(e => e.Category).Distinct().Order(), "Custom interests"];
    private static readonly string[] InterestRatingFilters = ["All ratings", "Favourite", "Yes", "Maybe", "No", "Unspecified"];

    private void DrawPreferences(ProfileBuilderDto builder)
    {
        builder.PreferenceAudience = Audience("Preference list audience", builder.PreferenceAudience);
        var catalogue = ProfilePreferenceCatalogue.Entries.ToDictionary(e => e.Id);
        var ratings = builder.Preferences.SelectMany(p => catalogue.GetValueOrDefault(p.CatalogueId ?? string.Empty)?.Directional == true || (p.CatalogueId == null && p.Directional)
            ? new[] { p.Doing, p.Receiving } : new[] { p.Rating }).ToArray();
        foreach (var rating in Enum.GetValues<ProfilePreferenceRating>().Where(r => r != ProfilePreferenceRating.Unspecified))
        {
            using var colour = ImRaii.PushColor(ImGuiCol.Text, InterestColour(rating));
            ImGui.TextUnformatted($"{rating}: {ratings.Count(r => r == rating)}");
            if (rating != ProfilePreferenceRating.No) ImGui.SameLine();
        }
        ImGui.TextWrapped("Doing and Receiving are rated separately.");
        var filtersChanged = ImGui.InputText("Search catalogue / aliases", ref _search, 160);
        filtersChanged |= ImGui.Combo("Category filter", ref _interestCategory, InterestCategories, InterestCategories.Length);
        filtersChanged |= ImGui.Combo("Rating filter", ref _interestRating, InterestRatingFilters, InterestRatingFilters.Length);
        filtersChanged |= ImGui.Checkbox("Rated entries only", ref _selectedOnly);
        var byCatalogue = builder.Preferences.Where(p => p.CatalogueId != null).ToDictionary(p => p.CatalogueId!);
        var matching = 0;
        var filtered = ProfilePreferenceCatalogue.Entries.Where(definition =>
            (_interestCategory == 0 || InterestCategories[_interestCategory] == definition.Category)
            && MatchesInterest(byCatalogue.GetValueOrDefault(definition.Id), definition.Directional)
            && (definition.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) || definition.Category.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || definition.Aliases.Any(a => a.Contains(_search, StringComparison.OrdinalIgnoreCase)))).GroupBy(d => d.Category);
        foreach (var group in filtered)
        {
            matching += group.Count();
            if (filtersChanged && (_interestCategory != 0 || _interestRating != 0 || _selectedOnly || _search.Length > 0))
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            if (!ImGui.CollapsingHeader($"{group.Key} ({group.Count()})###interest-category-{group.Key}")) continue;
            foreach (var definition in group)
            {
                var entry = byCatalogue.GetValueOrDefault(definition.Id);
                using var id = ImRaii.PushId(definition.Id);
                ImGui.TextUnformatted(entry is { Name.Length: > 0 } ? entry.Name : definition.Name);
                if (definition.Directional)
                {
                    var doing = InterestRatingButtons("Doing", entry?.Doing ?? ProfilePreferenceRating.Unspecified);
                    var receiving = InterestRatingButtons("Receiving", entry?.Receiving ?? ProfilePreferenceRating.Unspecified);
                    if (doing != (entry?.Doing ?? ProfilePreferenceRating.Unspecified) || receiving != (entry?.Receiving ?? ProfilePreferenceRating.Unspecified))
                    {
                        if (entry == null) { entry = new() { CatalogueId = definition.Id }; builder.Preferences.Add(entry); }
                        entry.Doing = doing; entry.Receiving = receiving;
                    }
                }
                else
                {
                    var rating = InterestRatingButtons("Rating", entry?.Rating ?? ProfilePreferenceRating.Unspecified);
                    if (rating != (entry?.Rating ?? ProfilePreferenceRating.Unspecified))
                    {
                        if (entry == null) { entry = new() { CatalogueId = definition.Id }; builder.Preferences.Add(entry); }
                        entry.Rating = rating;
                    }
                }
                if (entry != null)
                {
                    InterestNotes(entry);
                    ImGui.SameLine(); if (ImGui.SmallButton("Remove stored entry")) builder.Preferences.Remove(entry);
                }
                ImGui.Spacing();
            }
        }
        ImGui.Separator();
        if (ImGui.Button("Add custom interest"))
        {
            builder.Preferences.Add(new() { Name = "Custom interest" });
            _interestCategory = InterestCategories.Length - 1; _interestRating = 0;
            _selectedOnly = false; _search = string.Empty;
            filtersChanged = true;
        }
        var custom = builder.Preferences.Where(p => p.CatalogueId == null
            && (_interestCategory == 0 || InterestCategories[_interestCategory] == "Custom interests") && MatchesInterest(p, p.Directional)
            && p.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)).Reverse().ToArray();
        matching += custom.Length;
        if (custom.Length > 0)
        {
            if (filtersChanged && (_interestCategory != 0 || _interestRating != 0 || _selectedOnly || _search.Length > 0)) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            if (ImGui.CollapsingHeader($"Custom interests ({custom.Length})###interest-category-custom"))
            {
            foreach (var entry in custom)
            {
                using var id = ImRaii.PushId(entry.Id);
                entry.Name = Input("Custom interest name", entry.Name, 160);
                var directional = entry.Directional; if (ImGui.Checkbox("Separate doing / receiving", ref directional)) entry.Directional = directional;
                if (entry.Directional)
                {
                    entry.Doing = InterestRatingButtons("Doing", entry.Doing);
                    entry.Receiving = InterestRatingButtons("Receiving", entry.Receiving);
                }
                else entry.Rating = InterestRatingButtons("Rating", entry.Rating);
                InterestNotes(entry);
                ImGui.SameLine(); if (ImGui.SmallButton("Remove custom interest")) builder.Preferences.Remove(entry);
                if (ImGui.BeginCombo("Explicit catalogue link", "Choose a match"))
                {
                    foreach (var definition in ProfilePreferenceCatalogue.Entries.Where(d => d.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)))
                        if (ImGui.Selectable(definition.Name)) { entry.CatalogueId = definition.Id; break; }
                    ImGui.EndCombo();
                }
                ImGui.Separator();
            }
            }
        }
        ImGui.TextUnformatted($"{matching} matching interests.");
    }

    private bool MatchesInterest(ProfilePreferenceDto? entry, bool directional)
    {
        var values = directional ? new[] { entry?.Doing ?? 0, entry?.Receiving ?? 0 } : new[] { entry?.Rating ?? 0 };
        return (!_selectedOnly || values.Any(v => v != ProfilePreferenceRating.Unspecified)) &&
            (_interestRating == 0 || values.Contains(_interestRating == 5 ? ProfilePreferenceRating.Unspecified : (ProfilePreferenceRating)_interestRating));
    }

    private static void InterestNotes(ProfilePreferenceDto entry)
    {
        if (ImGui.SmallButton(string.IsNullOrEmpty(entry.Note) ? "Add note" : "Edit note")) ImGui.OpenPopup("interest-note");
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(entry.Note)) ImGui.SetTooltip(entry.Note);
        // Capture the parent width: remaining-width inputs feed back into an auto-sizing popup.
        var noteWidth = Math.Max(1f, Math.Min(420f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X));
        if (!ImGui.BeginPopup("interest-note")) return;
        ImGui.TextUnformatted("Explanatory note");
        var note = entry.Note;
        if (ImGui.InputTextMultiline("##explanatory-note", ref note, 2001, new Vector2(noteWidth, 120f * ImGuiHelpers.GlobalScale)))
            entry.Note = note;
        if (ImGui.Button("Done")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private static ProfilePreferenceRating InterestRatingButtons(string label, ProfilePreferenceRating value)
    {
        using var id = ImRaii.PushId(label);
        ImGui.TextDisabled(label);
        var perLine = ImGui.GetContentRegionAvail().X < 420 * ImGuiHelpers.GlobalScale ? 3 : 5;
        var choices = new[] { ProfilePreferenceRating.Favourite, ProfilePreferenceRating.Yes, ProfilePreferenceRating.Maybe, ProfilePreferenceRating.No, ProfilePreferenceRating.Unspecified };
        for (var i = 0; i < choices.Length; i++)
        {
            if (i % perLine != 0) ImGui.SameLine();
            var rating = choices[i]; var colour = InterestColour(rating); colour.W = value == rating ? .8f : .25f;
            using var background = ImRaii.PushColor(ImGuiCol.Button, colour);
            if (ImGui.SmallButton(value == rating ? $"[{rating}]" : rating.ToString())) value = rating;
        }
        return value;
    }

    private static Vector4 InterestColour(ProfilePreferenceRating rating) => rating switch
    {
        ProfilePreferenceRating.Favourite => new(.3f, .6f, 1, 1),
        ProfilePreferenceRating.Yes => new(.3f, .8f, .4f, 1),
        ProfilePreferenceRating.Maybe => new(1, .8f, .3f, 1),
        ProfilePreferenceRating.No => new(1, .4f, .4f, 1),
        _ => new(.6f, .6f, .6f, 1)
    };
}
