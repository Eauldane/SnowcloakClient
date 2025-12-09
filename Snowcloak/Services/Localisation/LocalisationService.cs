using Dalamud.Game;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace Snowcloak.Services.Localisation;

// Localisation service for Snowcloak. To use, inject the service into a window, and add a function like the below:
//
//    private string L(string key, string fallback)
//{
//    return _localisationService.GetString($"CompactUI.{key}", fallback);
//}
//
// Change the key by window.

public sealed class LocalisationService : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IClientState _clientState;
    private readonly ILogger<LocalisationService> _logger;
    private readonly Dictionary<string, string> _fallbackStrings;
    private Dictionary<string, string> _activeStrings = new(StringComparer.Ordinal);
    private string _activeLanguage = string.Empty;

    public LocalisationService(IDalamudPluginInterface pluginInterface, IClientState clientState, ILogger<LocalisationService> logger)
    {
        _pluginInterface = pluginInterface;
        _clientState = clientState;
        _logger = logger;
        _fallbackStrings = LoadLanguage("en");
        SetActiveLanguage(CurrentLanguageCode);
    }

    private string CurrentLanguageCode => _clientState.ClientLanguage switch
    {
        ClientLanguage.Japanese => "ja",
        ClientLanguage.French => "fr",
        ClientLanguage.German => "de",
        //ClientLanguage.ChineseSimplified => "zh",
        //ClientLanguage.Korean => "ko",

        _ => "en"
    };

    public string GetString(string key, string fallback)
    {
        var languageCode = CurrentLanguageCode;
        if (!string.Equals(_activeLanguage, languageCode, StringComparison.Ordinal))
        {
            SetActiveLanguage(languageCode);
        }

        if (_activeStrings.TryGetValue(key, out var text)) return text;
        if (_fallbackStrings.TryGetValue(key, out var fallbackText)) return fallbackText;
        return fallback;
    }

    private void SetActiveLanguage(string languageCode)
    {
        _activeLanguage = languageCode;
        var loadedStrings = LoadLanguage(languageCode);
        _activeStrings = loadedStrings.Count == 0 && !string.Equals(languageCode, "en", StringComparison.Ordinal)
            ? _fallbackStrings
            : loadedStrings;
    }

    private Dictionary<string, string> LoadLanguage(string languageCode)
    {
        try
        {
            var localisationRoot = Path.Combine(
                _pluginInterface.AssemblyLocation.DirectoryName ?? string.Empty,
                "Assets",
                "Localisation");

            var languageDirectory = Path.Combine(localisationRoot, languageCode);
            var languageFiles = new List<string>();

            if (Directory.Exists(languageDirectory))
            {
                languageFiles.AddRange(Directory.EnumerateFiles(languageDirectory, "*.json", SearchOption.TopDirectoryOnly));
            }

            var languageFile = Path.Combine(localisationRoot, $"{languageCode}.json");
            if (File.Exists(languageFile))
            {
                languageFiles.Add(languageFile);
            }

            if (languageFiles.Count == 0)
            {
                return new(StringComparer.Ordinal);
            }

            var localisedStrings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in languageFiles)
            {
                var json = File.ReadAllText(file);
                var parsedStrings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (parsedStrings == null)
                    continue;

                foreach (var pair in parsedStrings)
                {
                    localisedStrings[pair.Key] = pair.Value;
                }
            }

            return localisedStrings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load localisation file for language {LanguageCode}", languageCode);
            return new(StringComparer.Ordinal);
        }
    }
    
    public void Dispose()
    {
    }

}