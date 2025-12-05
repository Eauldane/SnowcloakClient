using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.Housing;

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
    private readonly ILogger<VenueRegistrationService> _logger;

    private HousingPlotLocation? _pendingPlot;
    private bool _wasPlacardOpen;
    private bool _loggedMissingPlacard;
    private string? _activePlacardAddonKey;

    public VenueRegistrationService(ILogger<VenueRegistrationService> logger, DalamudUtilService dalamudUtilService,
        IChatGui chatGui, IGameGui gameGui, IFramework framework, IObjectTable objectTable, IPlayerState playerState)
    {
        _logger = logger;
        _dalamudUtilService = dalamudUtilService;
        _chatGui = chatGui;
        _gameGui = gameGui;
        _framework = framework;
        _objectTable = objectTable;
        _playerState = _playerState;
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
            Message = $"[Snowcloak] Tracking placard for {location.DisplayName}. Interact with the placard to verify ownership.",
            Type = XivChatType.SystemMessage
        });

        _logger.LogInformation("Started venue registration flow for plot {Plot}", location.FullId);
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (_pendingPlot == null)
            return;

        if (!_dalamudUtilService.TryGetLastHousingPlot(out var currentPlot)
            || currentPlot.TerritoryId != _pendingPlot.Value.TerritoryId)
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

            var evaluation = await _dalamudUtilService.RunOnFrameworkThread(() =>
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
                    Message = "[Snowcloak] Placard opened, but no text could be read.",
                    Type = XivChatType.SystemMessage
                });
                return;
            }

            _chatGui.Print(new XivChatEntry
            {
                Message = "[Snowcloak] Placard details detected; evaluating ownership.",
                Type = XivChatType.SystemMessage
            });
            var (ward, plot) = ExtractWardAndPlot(evaluation.Lines);
            var plotMatches = ward == _pendingPlot.Value.WardId && plot == _pendingPlot.Value.PlotId;
            var (_, ownerValue) = ExtractLabelAndValue(evaluation.Lines, "owner");
            var (_, freeCompanyValue) = ExtractLabelAndValue(evaluation.Lines, "company");

            var matchesOwner = !string.IsNullOrWhiteSpace(evaluation.PlayerName)
                               && !string.IsNullOrWhiteSpace(ownerValue)
                               && ownerValue.Contains(evaluation.PlayerName, StringComparison.OrdinalIgnoreCase);


            var matchesFreeCompany = !string.IsNullOrWhiteSpace(evaluation.PlayerCompanyTag)
                                     && !string.IsNullOrWhiteSpace(freeCompanyValue)
                                     && freeCompanyValue.Contains(evaluation.PlayerCompanyTag,
                                         StringComparison.OrdinalIgnoreCase);

            if (ward != null || plot != null)
            {
                _chatGui.Print(new XivChatEntry
                {
                    Message =
                        $"[Snowcloak] Placard location Ward {ward?.ToString() ?? "?"} Plot {plot?.ToString() ?? "?"} vs tracked {_pendingPlot.Value.DisplayName}: {(plotMatches ? "match" : "mismatch")}",
                    Type = XivChatType.SystemMessage
                });
            }

            var authorized = matchesOwner || matchesFreeCompany;
            _chatGui.Print(new XivChatEntry
            {
                Message =
                    $"[Snowcloak] Authority check -> Owner match: {matchesOwner}, Free Company match: {matchesFreeCompany}. {(authorized ? "Registration can proceed." : "Registration blocked: no authority detected.")}",
                Type = XivChatType.SystemMessage
            });

            _chatGui.Print(new XivChatEntry
            {
                Message = authorized
                    ? "[Snowcloak] Ownership verification passed."
                    : "[Snowcloak] Ownership verification failed; no authorized owner detected.",
                Type = XivChatType.SystemMessage
            });

            if (!authorized && !plotMatches)
            {
                _chatGui.Print(new XivChatEntry
                {
                    Message =
                        "[Snowcloak] Placard does not match tracked plot; please verify you are registering the correct location.",
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

    private static (uint? Ward, uint? Plot) ExtractWardAndPlot(IEnumerable<string> lines)
    {
        uint? ward = null;
        uint? plot = null;

        foreach (var line in lines)
        {
            var wardMatch = Regex.Match(line, "Ward\\s*(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (wardMatch.Success && uint.TryParse(wardMatch.Groups[1].Value, out var wardValue))
                ward ??= wardValue;

            var plotMatch = Regex.Match(line, "Plot\\s*(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (plotMatch.Success && uint.TryParse(plotMatch.Groups[1].Value, out var plotValue))
                plot ??= plotValue;
        }

        return (ward, plot);
    }
    
    private static (string? Label, string? Value) ExtractLabelAndValue(IReadOnlyList<string> lines, string keyword)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = FindFirstValue(lines, i + 1);
            return (line, value);
        }

        return (null, null);
    }

    private static string? FindFirstValue(IReadOnlyList<string> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            var candidate = lines[i];
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (IsLikelyLabel(candidate))
                continue;

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
}