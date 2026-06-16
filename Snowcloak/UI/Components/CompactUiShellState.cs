using Snowcloak.Services.Mediator;
using System.Numerics;

namespace Snowcloak.UI.Components;

internal sealed class CompactUiShellState
{
    private Vector2 _lastPosition = Vector2.One;
    private Vector2 _lastSize = Vector2.One;
    private bool _wasOpen;

    public void CaptureCutsceneOpenState(bool isOpen)
    {
        _wasOpen = isOpen;
    }

    public bool RestoreCutsceneOpenState()
        => _wasOpen;

    public void PublishLayoutChange(Vector2 position, Vector2 size, SnowMediator mediator)
    {
        if (_lastSize == size && _lastPosition == position)
        {
            return;
        }

        _lastSize = size;
        _lastPosition = position;
        mediator.Publish(new CompactUiChange(_lastSize, _lastPosition));
    }
}
