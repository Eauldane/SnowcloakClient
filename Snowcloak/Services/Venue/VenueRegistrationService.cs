using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using ElezenTools.Housing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Housing;
using Snowcloak.Game.Scheduling;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Services.Venue;

/// <summary>
/// Drives venue ownership verification as an explicit flow:
/// Idle -> AwaitingPlacard -> Captured -> Submitting -> Idle.
/// Framework polling rides the shared frame scheduler (no raw OnFrameworkUpdate hook) and the unsafe
/// addon read is delegated to <see cref="HousingPlacardReader"/>, so this service stays a plain
/// state machine over clean inputs.
/// </summary>
public sealed class VenueRegistrationService : IHostedService, IDisposable
{
    private static readonly TimeSpan PlacardSettleDelay = TimeSpan.FromSeconds(2);

    private readonly ILogger<VenueRegistrationService> _logger;
    private readonly IChatGui _chatGui;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly SnowMediator _mediator;
    private readonly IFrameScheduler _frameScheduler;
    private readonly PlotPresenceTracker _plotPresence;
    private readonly HousingPlacardReader _placardReader;

    private RegistrationFlowState _state = RegistrationFlowState.Idle;
    private HousingPlotLocation _pendingPlot;
    private DateTime? _placardVisibleSinceUtc;
    private IFrameTickHandle? _tickHandle;

    public VenueRegistrationService(ILogger<VenueRegistrationService> logger, IChatGui chatGui,
        IObjectTable objectTable, IClientState clientState, SnowMediator mediator, IFrameScheduler frameScheduler,
        PlotPresenceTracker plotPresence, HousingPlacardReader placardReader)
    {
        _logger = logger;
        _chatGui = chatGui;
        _objectTable = objectTable;
        _clientState = clientState;
        _mediator = mediator;
        _frameScheduler = frameScheduler;
        _plotPresence = plotPresence;
        _placardReader = placardReader;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _tickHandle = _frameScheduler.Register("VenueRegistration", TickInterval.EveryMilliseconds(250),
            TickPriority.Low, OnTick);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _tickHandle?.Dispose();
        _tickHandle = null;
    }

    public bool IsRegistrationPending => _state != RegistrationFlowState.Idle;

    public void BeginRegistrationFromCommand()
    {
        if (!_plotPresence.TryGetCurrentPlot(out var location))
        {
            _chatGui.PrintError("[Snowcloak] You must stand on a housing plot to start registration.");
            return;
        }

        _pendingPlot = location;
        _placardVisibleSinceUtc = null;
        _state = RegistrationFlowState.AwaitingPlacard;

        _chatGui.Print(new XivChatEntry
        {
            Message = string.Format(CultureInfo.InvariantCulture,
                "[Snowcloak] Tracking placard for {0}. Interact with the placard and wait a few seconds to verify ownership.", location.FriendlyName),
            Type = XivChatType.SystemMessage
        });

        _logger.LogInformation("Started venue registration flow for plot {Plot}", location.FullId);
    }

    private void OnTick()
    {
        if (_state == RegistrationFlowState.Idle)
            return;

        // Bail out the moment the player leaves the plot they started on.
        if (!_plotPresence.TryGetCurrentPlot(out var currentPlot)
            || !PlotPresenceTracker.IsSameHousingStructure(currentPlot, _pendingPlot))
        {
            CancelPendingRegistration(
                "[Snowcloak] Registration cancelled: you changed areas. Please start registration again from the new plot.");
            return;
        }

        if (!_placardReader.IsPlacardVisible())
        {
            _placardVisibleSinceUtc = null;
            return;
        }

        // Rising edge: let the addon text settle before reading.
        if (_placardVisibleSinceUtc == null)
        {
            _placardVisibleSinceUtc = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - _placardVisibleSinceUtc.Value < PlacardSettleDelay)
            return;

        CapturePlacard();
    }

    private void CapturePlacard()
    {
        _state = RegistrationFlowState.Captured;

        if (!_placardReader.TryReadPlacard(out var lines))
        {
            _chatGui.Print(new XivChatEntry
            {
                Message = "[Snowcloak] Placard closed or unavailable before details loaded. Please open the placard again.",
                Type = XivChatType.SystemMessage
            });

            // Re-arm and keep waiting on the same plot.
            _placardVisibleSinceUtc = null;
            _state = RegistrationFlowState.AwaitingPlacard;
            return;
        }

        if (lines.Count == 0)
        {
            _chatGui.Print(new XivChatEntry
            {
                Message = "[Snowcloak] Placard opened, but no text could be read. This could be a bug!",
                Type = XivChatType.SystemMessage
            });

            _placardVisibleSinceUtc = null;
            _state = RegistrationFlowState.AwaitingPlacard;
            return;
        }

        _chatGui.Print(new XivChatEntry
        {
            Message = "[Snowcloak] Placard details detected; evaluating ownership.",
            Type = XivChatType.SystemMessage
        });

        var keywords = GetPlacardKeywords();
        var (ward, plot) = ExtractWardAndPlot(lines, keywords);
        var plotMatches = plot == _pendingPlot.PlotId;

        var playerName = _objectTable.LocalPlayer?.Name.TextValue;
        var playerCompanyTag = _objectTable.LocalPlayer?.CompanyTag?.TextValue;

        var evaluation = EvaluateOwnership(lines, keywords, playerName, playerCompanyTag);

#if DEBUG
        if (ward != null || plot != null)
        {
            _chatGui.Print(new XivChatEntry
            {
                Message =
                    $"[Snowcloak] Placard Ward {ward?.ToString() ?? "?"} Plot {plot?.ToString() ?? "?"} vs tracked {_pendingPlot.DisplayName}: {(plotMatches ? "match" : "mismatch")}",
                Type = XivChatType.SystemMessage
            });
        }
#endif

        SubmitOrReject(evaluation, plotMatches);
    }

    private void SubmitOrReject(OwnershipResult evaluation, bool plotMatches)
    {
        if (evaluation.MatchesOwner)
        {
            _chatGui.Print(new XivChatEntry
            {
                Message = "[Snowcloak] Authority check succeeded - you own this house. Registration can proceed.",
                Type = XivChatType.SystemMessage
            });
        }
        else if (evaluation.MatchesFreeCompany)
        {
            _chatGui.Print(new XivChatEntry
            {
                Message = "[Snowcloak] Authority check succeeded - your FC owns this house. Registration can proceed.",
                Type = XivChatType.SystemMessage
            });
        }

        if (evaluation.Authorised)
        {
            _state = RegistrationFlowState.Submitting;

            var context = new VenueRegistrationContext(_pendingPlot, evaluation.OwnerValue, evaluation.FreeCompanyTag, evaluation.MatchesFreeCompany);
            _mediator.Publish(new OpenVenueRegistrationWindowMessage(context));

            _chatGui.Print(new XivChatEntry
            {
                Message = "[Snowcloak] Opening venue registration window.",
                Type = XivChatType.SystemMessage
            });
        }
        else
        {
            _chatGui.Print(new XivChatEntry
            {
                Message = "[Snowcloak] Authority check failed - this plot doesn't seem to belong to you.",
                Type = XivChatType.SystemMessage
            });

            if (!plotMatches)
            {
                _chatGui.Print(new XivChatEntry
                {
                    Message = "[Snowcloak] Placard does not match tracked plot; please verify you are registering the correct location.",
                    Type = XivChatType.SystemMessage
                });
            }
        }

        // A read attempt completes the flow either way; the user can re-run /venue register to retry.
        ResetToIdle();
    }

    private void CancelPendingRegistration(string message)
    {
        ResetToIdle();

        _chatGui.Print(new XivChatEntry
        {
            Message = message,
            Type = XivChatType.SystemMessage
        });

        _logger.LogInformation("Cleared pending registration: {Message}", message);
    }

    private void ResetToIdle()
    {
        _state = RegistrationFlowState.Idle;
        _pendingPlot = default;
        _placardVisibleSinceUtc = null;
    }

    private readonly record struct OwnershipResult(bool MatchesOwner, bool MatchesFreeCompany, string? OwnerValue, string? FreeCompanyTag)
    {
        public bool Authorised => MatchesOwner || MatchesFreeCompany;
    }

    private OwnershipResult EvaluateOwnership(IReadOnlyList<string> lines, PlacardKeywords keywords,
        string? playerName, string? playerCompanyTag)
    {
        var ownerResult = ExtractLabelAndValue(lines, keywords.OwnerKeywords);
        var ownerValue = ownerResult.Value;
        var (_, freeCompanyValue, _, _) = ExtractLabelAndValue(lines, keywords.CompanyKeywords);

        var matchesOwnerName = !string.IsNullOrWhiteSpace(playerName)
                               && !string.IsNullOrWhiteSpace(ownerValue)
                               && ownerValue!.Contains(playerName!, StringComparison.OrdinalIgnoreCase);

        var matchesOwnerCompanyTag = !string.IsNullOrWhiteSpace(playerCompanyTag)
                                     && !string.IsNullOrWhiteSpace(ownerValue)
                                     && MatchesFreeCompanyTag(ownerValue!, playerCompanyTag!);

        var ownerTagLine = !matchesOwnerName && ownerResult.ValueIndex >= 0
            ? FindFirstValue(lines, ownerResult.ValueIndex + 1, out _)
            : null;

        var companySource = freeCompanyValue ?? ownerTagLine ?? ownerValue;

        var matchesFreeCompany = !string.IsNullOrWhiteSpace(playerCompanyTag)
                                 && !string.IsNullOrWhiteSpace(companySource)
                                 && MatchesFreeCompanyTag(companySource!, playerCompanyTag!);

        var matchesOwner = matchesOwnerName || matchesOwnerCompanyTag;

        var freeCompanyTag = matchesFreeCompany
            ? ExtractFreeCompanyTag(companySource ?? freeCompanyValue, playerCompanyTag)
            : ExtractFreeCompanyTag(freeCompanyValue, null);

        return new OwnershipResult(matchesOwner, matchesFreeCompany, ownerValue, freeCompanyTag);
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

    private static (uint? Ward, uint? Plot) ExtractWardAndPlot(IEnumerable<string> lines, PlacardKeywords placardKeywords)
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
        var filtered = value.Where(c =>
        {
            if (char.IsControl(c))
                return false;

            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            return category != UnicodeCategory.LineSeparator
                   && category != UnicodeCategory.ParagraphSeparator
                   && category != UnicodeCategory.PrivateUse;
        });

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

    private static bool IsLikelyLabel(string text)
    {
        if (text.Length <= 2)
            return true;

        var lowered = text.ToLowerInvariant();
        if (lowered.Contains(':'))
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

    private enum RegistrationFlowState
    {
        Idle,
        AwaitingPlacard,
        Captured,
        Submitting,
    }
}
