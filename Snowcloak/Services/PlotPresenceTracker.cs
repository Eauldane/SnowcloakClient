using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using ElezenTools.Housing;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.Mediator;

namespace Snowcloak.Services;

public sealed partial class PlotPresenceTracker
{
    private readonly ILogger<PlotPresenceTracker> _logger;
    private readonly IObjectTable _objectTable;
#if DEBUG
    private readonly IChatGui _chatGui;
#endif
    private readonly SnowMediator _mediator;

    private bool _isOnPlot;
    private HousingPlotLocation _currentPlot;

    public PlotPresenceTracker(
        ILogger<PlotPresenceTracker> logger,
        IObjectTable objectTable,
        IChatGui chatGui,
        SnowMediator mediator)
    {
        _logger = logger;
        _objectTable = objectTable;
#if DEBUG
        _chatGui = chatGui;
#endif
        _mediator = mediator;
    }

    public string HousingString => _currentPlot.FullId;

    public bool TryGetCurrentPlot(out HousingPlotLocation location)
    {
        location = _currentPlot;
        return _isOnPlot;
    }

    public void Tick()
    {
        if (_objectTable.LocalPlayer == null)
            return;

        var isCurrentlyOnPlot = PlayerInteractionService.TryGetHousingPlotLocation(out var currentLocation, out var isInsideHousing);

        if (_isOnPlot && isCurrentlyOnPlot && isInsideHousing
            && !currentLocation.Equals(_currentPlot)
            && IsSameHousingStructure(currentLocation, _currentPlot))
        {
            currentLocation = _currentPlot;
        }

        if (_isOnPlot && (!isCurrentlyOnPlot || !currentLocation.Equals(_currentPlot)))
        {
            LogExitedHousingPlot(_logger, _currentPlot.FullId);
#if DEBUG
            _chatGui.Print(new XivChatEntry
            {
                Message = $"Exited housing plot {_currentPlot.DisplayName}",
                Type = XivChatType.SystemMessage
            });
#endif
            _mediator.Publish(new HousingPlotLeftMessage(_currentPlot));
        }

        if (isCurrentlyOnPlot && (!_isOnPlot || !currentLocation.Equals(_currentPlot)))
        {
            LogEnteredHousingPlot(_logger, currentLocation.FullId);
#if DEBUG
            _chatGui.Print(new XivChatEntry
            {
                Message = $"Entered housing plot {currentLocation.DisplayName}",
                Type = XivChatType.SystemMessage
            });
#endif
            _mediator.Publish(new HousingPlotEnteredMessage(currentLocation));
        }

        _isOnPlot = isCurrentlyOnPlot;
        _currentPlot = currentLocation;
    }


    public static bool IsSameHousingStructure(HousingPlotLocation left, HousingPlotLocation right)
    {
        return left.WorldId == right.WorldId
               && left.WardId == right.WardId
               && left.PlotId == right.PlotId
               && left.IsApartment == right.IsApartment;
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Exited housing plot {FullId}")]
    private static partial void LogExitedHousingPlot(ILogger logger, string fullId);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Entered housing plot {FullId}")]
    private static partial void LogEnteredHousingPlot(ILogger logger, string fullId);
}
