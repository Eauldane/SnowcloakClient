using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text.SeStringHandling;
using ElezenTools.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.Housing;
using Snowcloak.Services.Mediator;


namespace Snowcloak.Services.Venue;

public sealed class VenueRegistrationService : IHostedService, IDisposable
{
    private const string PlacardAddonName = "HousingSignBoard";

    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IChatGui _chatGui;
    private readonly IGameGui _gameGui;
    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IPlayerState _playerState;
    private readonly IClientState _clientState;
    private readonly ILogger<VenueRegistrationService> _logger;
    private readonly SnowMediator _mediator;
    
    private HousingPlotLocation? _pendingPlot;
    private bool _wasPlacardOpen;
    private bool _loggedMissingPlacard;
    private string? _activePlacardAddonKey;

    public VenueRegistrationService(ILogger<VenueRegistrationService> logger, DalamudUtilService dalamudUtilService,
        IChatGui chatGui, IGameGui gameGui, IFramework framework, IObjectTable objectTable, IPlayerState playerState,
        IClientState clientState, SnowMediator mediator)
    {
        _logger = logger;
        _dalamudUtilService = dalamudUtilService;
        _chatGui = chatGui;
        _gameGui = gameGui;
        _framework = framework;
        _objectTable = objectTable;
        _playerState = playerState;
        _clientState = clientState;
        _mediator = mediator;
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _framework.Update += OnFrameworkUpdate;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _framework.Update -= OnFrameworkUpdate;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
    }

    public bool IsRegistrationPending => _pendingPlot != null;

    public void BeginRegistrationFromCommand()
    {
        if (!_dalamudUtilService.TryGetLastHousingPlot(out var location))
        {
            _chatGui.PrintError("[Snowcloak] You must stand on a housing plot to start registration.");
            return;
        }

        _pendingPlot = location;
        _wasPlacardOpen = false;
        _loggedMissingPlacard = false;
        _activePlacardAddonKey = null;
        _chatGui.Print(new XivChatEntry
        {
            Message = string.Format(CultureInfo.InvariantCulture,
                "[Snowcloak] Tracking placard for {0}. Interact with the placard and wait a few seconds to verify ownership.", location.FriendlyName),
            Type = XivChatType.SystemMessage
        });

        _logger.LogInformation("Started venue registration flow for plot {Plot}", location.FullId);
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (_pendingPlot == null)
            return;

        var hasCurrentPlot = _dalamudUtilService.TryGetLastHousingPlot(out var currentPlot);
        var pendingPlot = _pendingPlot.Value;

        var locationInfo = _dalamudUtilService.GetMapData();
        var movedToDifferentTerritory = _clientState.TerritoryType != pendingPlot.TerritoryId;
        var movedToDifferentSubdivision = locationInfo.DivisionId != pendingPlot.DivisionId
                                          || locationInfo.WardId != pendingPlot.WardId;
        var enteredDifferentPlot = hasCurrentPlot && !IsSameHousingStructure(currentPlot, pendingPlot);

        if (movedToDifferentTerritory || movedToDifferentSubdivision || enteredDifferentPlot)
        {
            CancelPendingRegistration(
                "[Snowcloak] Registration cancelled: you changed areas. Please start registration again from the new plot.");
            return;
        }

        var addon = TryGetPlacardAddon(out var addonIndex);
        
        if (addon == null)
        {
            if (!_loggedMissingPlacard)
            {
                _logger.LogDebug("Housing placard addon not found at indices 0 or 1.");
                _loggedMissingPlacard = true;
            }

            _wasPlacardOpen = false;
            return;
        }
        var addonKey = addonIndex != null ? $"{PlacardAddonName}@{addonIndex}" : null;
        if (addonKey != null && addonKey != _activePlacardAddonKey)
        {
            _logger.LogDebug("Using placard addon {AddonName} at index {Index} for validation.", PlacardAddonName, addonIndex);
            _activePlacardAddonKey = addonKey;
        }
        _loggedMissingPlacard = false;
        var placardOpen = addon->IsVisible;

        if (!placardOpen)
        {
            _wasPlacardOpen = false;
            return;
        }

        if (_wasPlacardOpen)
            return;

        _wasPlacardOpen = true;
        _ = HandlePlacardOpenedAsync();
    }

    private static bool IsSameHousingStructure(HousingPlotLocation left, HousingPlotLocation right)
    {
        return left.WorldId == right.WorldId
               && left.WardId == right.WardId
               && left.PlotId == right.PlotId
               && left.IsApartment == right.IsApartment;
    }
    
    private void CancelPendingRegistration(string message)
    {
        _pendingPlot = null;
        _wasPlacardOpen = false;
        _loggedMissingPlacard = false;
        _activePlacardAddonKey = null;

        _chatGui.Print(new XivChatEntry
        {
            Message = message,
            Type = XivChatType.SystemMessage
        });

        _logger.LogInformation("Cleared pending registration: {Message}", message);
    }

    private async Task HandlePlacardOpenedAsync()
    {
        if (_pendingPlot == null)
            return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var evaluation = await Service.UseFramework(() =>
            {
                unsafe
                {
                    if (_pendingPlot == null)
                        return PlacardEvaluationData.Unavailable;

                    var addon = TryGetPlacardAddon(out _);
                    if (addon == null || !addon->IsVisible)
                        return PlacardEvaluationData.Unavailable;

                    var lines = ExtractPlacardLines(addon);
                    var player = _objectTable.LocalPlayer;
                    var playerName = player?.Name.TextValue;
                    var playerCompanyTag = player?.CompanyTag?.TextValue;

                    return new PlacardEvaluationData(lines, playerName, playerCompanyTag, true);
                }
            }).ConfigureAwait(false);

            if (!evaluation.PlacardVisible)
            {
                _chatGui.Print(new XivChatEntry
                {
                    Message =
                        "[Snowcloak] Placard closed or unavailable before details loaded. Please open the placard again.",
                    Type = XivChatType.SystemMessage
                });

                _wasPlacardOpen = false;
                return;
            }

            if (evaluation.Lines == null || evaluation.Lines.Count == 0)
            {
                _chatGui.Print(new XivChatEntry
                {
                    Message = "[Snowcloak] Placard opened, but no text could be read. This could be a bug!",
                    Type = XivChatType.SystemMessage
                });
                return;
            }

            _chatGui.Print(new XivChatEntry
            {
                Message = "[Snowcloak] Placard details detected; evaluating ownership.",
                Type = XivChatType.SystemMessage
            });
            var placardKeywords = GetPlacardKeywords();

            var (ward, plot) = ExtractWardAndPlot(evaluation.Lines, placardKeywords);
            var plotMatches = plot == _pendingPlot.Value.PlotId;
            var ownerResult = ExtractLabelAndValue(evaluation.Lines, placardKeywords.OwnerKeywords);
            var ownerValue = ownerResult.Value;
            var (_, freeCompanyValue, _, _) = ExtractLabelAndValue(evaluation.Lines, placardKeywords.CompanyKeywords);

            
            var matchesOwnerName = !string.IsNullOrWhiteSpace(evaluation.PlayerName)
                                   && !string.IsNullOrWhiteSpace(ownerValue)
                                   && ownerValue.Contains(evaluation.PlayerName, StringComparison.OrdinalIgnoreCase);

            var matchesOwnerCompanyTag = !string.IsNullOrWhiteSpace(evaluation.PlayerCompanyTag)
                                         && !string.IsNullOrWhiteSpace(ownerValue)
                                         && MatchesFreeCompanyTag(ownerValue!, evaluation.PlayerCompanyTag!);
            
            var ownerTagLine = !matchesOwnerName && ownerResult.ValueIndex >= 0
                ? FindFirstValue(evaluation.Lines, ownerResult.ValueIndex + 1, out _)
                : null;


            var companySource = freeCompanyValue ?? ownerTagLine ?? ownerValue;

            var matchesFreeCompany = !string.IsNullOrWhiteSpace(evaluation.PlayerCompanyTag)
                                     && !string.IsNullOrWhiteSpace(companySource)
                                     && MatchesFreeCompanyTag(companySource!, evaluation.PlayerCompanyTag!);

            var matchesOwner = matchesOwnerName || matchesOwnerCompanyTag;
#if DEBUG
            if (ward != null || plot != null)
            {
                _chatGui.Print(new XivChatEntry
                {
                    Message =
                        $"[Snowcloak] Placard location Ward {ward?.ToString() ?? "?"} Plot {plot?.ToString() ?? "?"} vs tracked {_pendingPlot.Value.DisplayName}: {(plotMatches ? "match" : "mismatch")}",
                    Type = XivChatType.SystemMessage
                });
            }
#endif
            var authorised = matchesOwner || matchesFreeCompany;
            if (matchesOwner) {
                _chatGui.Print(new XivChatEntry
                {
                    Message =
                        "[Snowcloak] Authority check succeeded - you own this house. Registration can proceed.",
                    Type = XivChatType.SystemMessage
                });
            }
            if (matchesFreeCompany) {
                _chatGui.Print(new XivChatEntry
                {
                    Message =
                        "[Snowcloak] Authority check succeeded - your FC owns this house. Registration can proceed.",
                    Type = XivChatType.SystemMessage
                });
            }

            if (!authorised)
            {
                _chatGui.Print(new XivChatEntry
                {
                    Message =
                        "[Snowcloak] Authority check failed - this plot doesn't seem to belong to you.",
                    Type = XivChatType.SystemMessage
                });
            }
            

            if (!authorised && !plotMatches)
            {
                _chatGui.Print(new XivChatEntry
                {
                    Message =
                        "[Snowcloak] Placard does not match tracked plot; please verify you are registering the correct location.",
                    Type = XivChatType.SystemMessage
                });
            }

            if (authorised)
            {
                var freeCompanyTag = matchesFreeCompany
                    ? ExtractFreeCompanyTag(companySource ?? freeCompanyValue, evaluation.PlayerCompanyTag)
                    : ExtractFreeCompanyTag(freeCompanyValue, null);
                var context = new VenueRegistrationContext(_pendingPlot.Value, ownerValue, freeCompanyTag, matchesFreeCompany);

                _mediator.Publish(new OpenVenueRegistrationWindowMessage(context));

                _chatGui.Print(new XivChatEntry
                {
                    Message = "[Snowcloak] Opening venue registration window.",
                    Type = XivChatType.SystemMessage
                });
            }

            _pendingPlot = null;
            _wasPlacardOpen = false;
        }
        catch (Exception ex)
        {
            _wasPlacardOpen = false;
            _logger.LogError(ex, "Error while handling placard open.");
        }
    }
    private sealed record PlacardEvaluationData(List<string>? Lines, string? PlayerName, string? PlayerCompanyTag, bool PlacardVisible)
    {
        public static readonly PlacardEvaluationData Unavailable = new(null, null, null, false);
    }

    private unsafe AtkUnitBase* TryGetPlacardAddon(out int? addonIndex)
    {
        addonIndex = null;

        var addon = _gameGui.GetAddonByName<AtkUnitBase>(PlacardAddonName);
        if (addon != null)
        {
            addonIndex = 0;
            return addon;
        }

        addon = _gameGui.GetAddonByName<AtkUnitBase>(PlacardAddonName, 1);
        if (addon != null)
        {
            addonIndex = 1;
            return addon;
        }
        return null;
    }

    
    private unsafe List<string> ExtractPlacardLines(AtkUnitBase* addon)    {
        var lines = new List<string>();

        if (addon == null)
            return lines;

        var visited = new HashSet<nint>();
        var stack = new Stack<nint>();

        void PushNode(AtkResNode* node)
        {
            if (node == null)
                return;

            var key = (nint)node;
            if (visited.Contains(key))
                return;

            visited.Add(key);
            stack.Push(key);
        }

        PushNode(addon->RootNode);
        
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            PushNode(addon->UldManager.NodeList[i]);

        while (stack.Count > 0)
        {
            var node = (AtkResNode*)stack.Pop();
            
            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                var text = ReadNodeText(textNode);
                
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add(text);
            }
            else if (node->Type == NodeType.Component)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                {
                    PushNode(component->UldManager.RootNode);
                    for (var i = 0; i < component->UldManager.NodeListCount; i++)
                        PushNode(component->UldManager.NodeList[i]);
                }
            }

            PushNode(node->ChildNode);
            PushNode(node->PrevSiblingNode);
            PushNode(node->NextSiblingNode);
        }

        for (var i = 0; i < lines.Count; i++)
                _logger.LogInformation("Placard extracted line {LineIndex}: {Text}", i, lines[i]);

        return lines;
    }

    private static unsafe string ReadNodeText(AtkTextNode* textNode)
    {
        var text = textNode->NodeText.ToString();
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        if (textNode->NodeText.StringPtr != (byte*)null)
        {
            var span = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(textNode->NodeText.StringPtr);

            try
            {
                var seString = SeString.Parse(span);
                if (!string.IsNullOrWhiteSpace(seString.TextValue))
                    return seString.TextValue;
            }
            catch
            {
                // ignored – fallback to empty
            }
        }

        return string.Empty;
    }

    private unsafe void LogPlacardNode(AtkResNode* node, string? text = null)
    {

        var address = ((nint)node).ToString("X");
        var typeName = node->Type.ToString();

        if (!string.IsNullOrWhiteSpace(text))
        {
            _logger.LogInformation("Placard node {NodeType} @0x{NodeAddress} text: {Text}", typeName, address, text);
            return;
        }

        _logger.LogInformation("Placard node {NodeType} @0x{NodeAddress} visited", typeName, address);
    }

      private PlacardKeywords GetPlacardKeywords()
    {
        return _clientState.ClientLanguage switch
        {
            ClientLanguage.German => new PlacardKeywords(
                new[] { "Bezirk" },
                new[] { "Grundstück" },
                new[] { "Besitzer", "Eigentümer" },
                new[] { "Freie Gesellschaft" }),
            ClientLanguage.French => new PlacardKeywords(
                new[] { "Secteur" },
                new[] { "Parcelle", "Terrain" },
                new[] { "Propriétaire" },
                new[] { "Compagnie libre" }),
            ClientLanguage.Japanese => new PlacardKeywords(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { "Owner" },
                new[] { "フリーカンパニー" },
                "第?\\s*([0-9０-９]+)\\s*区",
                "第?\\s*([0-9０-９]+)\\s*番地"),
            _ => new PlacardKeywords(
                new[] { "Ward" },
                new[] { "Plot" },
                new[] { "Owner" },
                new[] { "Company" })
        };
    }

    private static uint? TryParseLocalizedNumber(string value)
    {
        Span<char> normalized = stackalloc char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            normalized[i] = ch switch
            {
                >= '０' and <= '９' => (char)('0' + (ch - '０')),
                _ => ch
            };
        }

        return uint.TryParse(normalized, out var result) ? result : null;
    }

    private static uint? TryMatchNumber(string line, IReadOnlyList<string> keywords, string? customPattern = null)
    {
        const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        if (!string.IsNullOrWhiteSpace(customPattern))
        {
            var match = Regex.Match(line, customPattern, Options);
            if (match.Success)
                return TryParseLocalizedNumber(match.Groups[1].Value);
        }

        foreach (var keyword in keywords)
        {
            var match = Regex.Match(line, $"{keyword}\\s*([0-9０-９]+)", Options);
            if (match.Success)
                return TryParseLocalizedNumber(match.Groups[1].Value);
        }

        return null;
    }

    private (uint? Ward, uint? Plot) ExtractWardAndPlot(IEnumerable<string> lines, PlacardKeywords placardKeywords)
    {
        uint? ward = null;
        uint? plot = null;

        foreach (var line in lines)
        {
            var wardValue = TryMatchNumber(line, placardKeywords.WardKeywords, placardKeywords.WardPattern);
            if (wardValue != null)
                ward ??= wardValue;

            var plotValue = TryMatchNumber(line, placardKeywords.PlotKeywords, placardKeywords.PlotPattern);
            if (plotValue != null)
                plot ??= plotValue;
        }

        return (ward, plot);
    }

    private static (string? Label, string? Value, int LabelIndex, int ValueIndex) ExtractLabelAndValue(
        IReadOnlyList<string> lines, IReadOnlyList<string> keywords)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!keywords.Any(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                continue;

            var value = FindFirstValue(lines, i + 1, out var valueIndex);
            return (line, value, i, valueIndex);
        }

        return (null, null, -1, -1);
    }

    private static bool MatchesFreeCompanyTag(string placardValue, string playerTag)
    {
        var normalizedPlayer = NormalizeFreeCompanyTag(playerTag);
        if (string.IsNullOrEmpty(normalizedPlayer))
            return false;

        placardValue = StripFormattingCharacters(placardValue);

        var placardTagMatch = Regex.Match(placardValue, "<<\\s*([^<>]+?)\\s*>>|«\\s*([^«»]+?)\\s*»",
            RegexOptions.CultureInvariant);

        if (placardTagMatch.Success)
        {
            var captured = placardTagMatch.Groups[1].Success ? placardTagMatch.Groups[1].Value : placardTagMatch.Groups[2].Value;
            var normalizedPlacardTag = NormalizeFreeCompanyTag(captured);
            if (!string.IsNullOrEmpty(normalizedPlacardTag))
                return normalizedPlayer.Equals(normalizedPlacardTag, StringComparison.OrdinalIgnoreCase);
        }

        return placardValue.Contains(playerTag, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripFormattingCharacters(string value)
    {
        var filtered = value.Where(c => !char.IsControl(c) && c != '\u2028' && c != '\u2029'
                                        && (c < '\uE000' || c > '\uF8FF'));

        return new string(filtered.ToArray());
    }

    private static string NormalizeFreeCompanyTag(string value)
    {
        var filtered = value.Where(char.IsLetterOrDigit);
        return new string(filtered.ToArray());
    }

    private static string? FindFirstValue(IReadOnlyList<string> lines, int startIndex, out int foundIndex)
    {
        foundIndex = -1;

        for (var i = startIndex; i < lines.Count; i++)
        {
            var candidate = lines[i];
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (IsLikelyLabel(candidate))
                continue;

            foundIndex = i;
            return candidate;
        }

        return null;
    }

    // Don't fucking judge me for keeping this here this was a goddamn nightmare
    private static bool IsLikelyLabel(string text)
    {
        if (text.Length <= 2)
            return true;

        var lowered = text.ToLowerInvariant();
        if (lowered.Contains(":"))
            return false;

        string[] templateKeywords =
        {
            "plot", "address", "price", "devaluation", "greeting", "name", "tag", "details", "owner", "company",
            "estate", "hall", "main", "sub", "size", "next", "ward"
        };

        return templateKeywords.Any(k => lowered.Contains(k));
    }
    
    
    private static string? ExtractFreeCompanyTag(string? source, string? fallbackTag)
    {
        if (!string.IsNullOrWhiteSpace(source))
        {
            var stripped = StripFormattingCharacters(source);
            var match = Regex.Match(stripped, "<<\\s*([^<>]+?)\\s*>>|«\\s*([^«»]+?)\\s*»", RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var captured = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var normalized = NormalizeFreeCompanyTag(captured);
                if (!string.IsNullOrWhiteSpace(normalized))
                    return normalized;
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackTag))
        {
            var normalized = NormalizeFreeCompanyTag(fallbackTag);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return null;
    }

    private sealed record PlacardKeywords(
        IReadOnlyList<string> WardKeywords,
        IReadOnlyList<string> PlotKeywords,
        IReadOnlyList<string> OwnerKeywords,
        IReadOnlyList<string> CompanyKeywords,
        string? WardPattern = null,
        string? PlotPattern = null);
}