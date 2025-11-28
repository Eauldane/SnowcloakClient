using System.Numerics;

namespace Snowcloak.UI.Components.Popup;

public interface IPopupHandler
{
    Vector2 PopupSize { get; }
    bool ShowClose { get; }

    void DrawContent();
}