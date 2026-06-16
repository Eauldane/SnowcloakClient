using Snowcloak.PlayerData.Handlers;

namespace Snowcloak.UI;

public sealed class TransferOverlayUiState
{
    private readonly Lock _gate = new();
    private readonly HashSet<GameObjectHandler> _uploadingPlayers = [];

    public bool EditTrackerPosition { get; set; }

    public bool HasUploadingPlayers
    {
        get
        {
            lock (_gate)
            {
                return _uploadingPlayers.Count > 0;
            }
        }
    }

    public void SetUploading(GameObjectHandler handler, bool isUploading)
    {
        lock (_gate)
        {
            if (isUploading)
            {
                _uploadingPlayers.Add(handler);
                return;
            }

            _uploadingPlayers.Remove(handler);
        }
    }

    public IReadOnlyList<GameObjectHandler> UploadingPlayersSnapshot
    {
        get
        {
            lock (_gate)
            {
                return [.. _uploadingPlayers];
            }
        }
    }
}
