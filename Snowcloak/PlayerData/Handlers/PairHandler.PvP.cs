namespace Snowcloak.PlayerData.Handlers;

public sealed partial class PairHandler
{
    internal bool IsSyncSuppressedForPvP => _dalamudUtil.IsSyncSuppressedForPvP;
    internal bool NeedsPvPResume { get; private set; }

    private void UpdatePvPSync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        if (IsSyncSuppressedForPvP)
        {
            NeedsPvPResume = true;
            _downloadFlight.Cancel();
            _applicationFlight.Cancel();
            _appliedState.RequireModRecovery();
        }
        else if (IsVisible && _dalamudUtil.CanReadCharacterObjects)
        {
            _dataReceivedInDowntime = null;
            _ = _backgroundTasks.Run(() =>
            {
                if (Volatile.Read(ref _disposed) == 0 && !IsSyncSuppressedForPvP
                    && _dalamudUtil.CanReadCharacterObjects)
                    Pair.ApplyLastReceivedData(forced: true);
                return Task.CompletedTask;
            }, nameof(UpdatePvPSync));
        }
    }
}
