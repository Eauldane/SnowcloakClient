using Snowcloak.PlayerData.Handlers;
using Snowcloak.WebAPI.Files.Models;

namespace Snowcloak.WebAPI.Files;

public sealed class DownloadStatusStore
{
    private readonly Lock _gate = new();
    private readonly List<DownloadTracker> _active = [];
    private int _incompleteNetworkDownloadCount;

    public DownloadStatusHandle Begin(GameObjectHandler handler, string? uid)
    {
        var tracker = new DownloadTracker(handler, uid, AdjustIncompleteNetworkDownloadCount);
        lock (_gate)
        {
            _active.Add(tracker);
        }

        return new DownloadStatusHandle(this, tracker);
    }

    public bool HasActiveDownloads
    {
        get
        {
            lock (_gate)
            {
                return _active.Count > 0;
            }
        }
    }

    public bool HasIncompleteNetworkDownloads => Volatile.Read(ref _incompleteNetworkDownloadCount) > 0;

    public IReadOnlyList<DownloadSnapshot> Snapshot()
    {
        DownloadTracker[] trackers;
        lock (_gate)
        {
            trackers = [.. _active];
        }

        return Array.ConvertAll(trackers, t => t.Snapshot());
    }

    public DownloadSnapshot? SnapshotForUid(string uid)
    {
        DownloadTracker? tracker;
        lock (_gate)
        {
            tracker = _active.FindLast(t => string.Equals(t.Uid, uid, StringComparison.Ordinal));
        }

        return tracker?.Snapshot();
    }

    private void Remove(DownloadTracker tracker)
    {
        lock (_gate)
        {
            if (_active.Remove(tracker))
                tracker.StopTrackingNetworkDownloads();
        }
    }

    private void AdjustIncompleteNetworkDownloadCount(int delta)
        => Interlocked.Add(ref _incompleteNetworkDownloadCount, delta);

    internal sealed class DownloadTracker
    {
        private readonly Lock _gate = new();
        private readonly List<DownloadGroupState> _groups = [];
        private readonly Action<int> _adjustIncompleteNetworkDownloadCount;
        private bool _trackNetworkDownloads = true;

        public DownloadTracker(GameObjectHandler handler, string? uid, Action<int> adjustIncompleteNetworkDownloadCount)
        {
            Handler = handler;
            Uid = uid;
            _adjustIncompleteNetworkDownloadCount = adjustIncompleteNetworkDownloadCount;
        }

        public GameObjectHandler Handler { get; }
        public string? Uid { get; }

        public DownloadGroupState AddGroup(string server, long totalBytes, int totalFiles)
        {
            lock (_gate)
            {
                var state = new DownloadGroupState(server, totalBytes, totalFiles,
                    _trackNetworkDownloads ? _adjustIncompleteNetworkDownloadCount : null);
                _groups.Add(state);
                return state;
            }
        }

        public DownloadSnapshot Snapshot()
        {
            DownloadGroupState[] groups;
            lock (_gate)
            {
                groups = [.. _groups];
            }

            return new DownloadSnapshot(Handler, Uid, Array.ConvertAll(groups, g => g.Snapshot()));
        }

        public void StopTrackingNetworkDownloads()
        {
            lock (_gate)
            {
                if (!_trackNetworkDownloads) return;
                _trackNetworkDownloads = false;

                foreach (var group in _groups)
                    group.StopTrackingNetworkDownload();
            }
        }
    }

    internal sealed class DownloadGroupState
    {
        private readonly Lock _gate = new();
        private readonly string _server;
        private readonly int _totalFiles;
        private DownloadStatus _status = DownloadStatus.Initializing;
        private long _transferredBytes;
        private long _totalBytes;
        private int _transferredFiles;
        private string? _statusMessage;
        private Action<int>? _adjustIncompleteNetworkDownloadCount;
        private bool _networkDownloadIncomplete;

        public DownloadGroupState(string server, long totalBytes, int totalFiles,
            Action<int>? adjustIncompleteNetworkDownloadCount)
        {
            _server = server;
            _totalBytes = totalBytes;
            _totalFiles = totalFiles;
            _adjustIncompleteNetworkDownloadCount = adjustIncompleteNetworkDownloadCount;
            _networkDownloadIncomplete = IsNetworkDownloadIncomplete();
            if (_networkDownloadIncomplete)
                _adjustIncompleteNetworkDownloadCount?.Invoke(1);
        }

        public void SetStatus(DownloadStatus status)
        {
            lock (_gate)
            {
                _status = status;
            }
        }

        public void SetUnavailable(string message)
        {
            lock (_gate)
            {
                _status = DownloadStatus.Unavailable;
                _statusMessage = message;
            }
        }

        public void SetTotalBytes(long totalBytes)
        {
            lock (_gate)
            {
                _totalBytes = totalBytes;
                UpdateNetworkDownloadState();
            }
        }

        public void AddBytes(long bytes)
        {
            lock (_gate)
            {
                _transferredBytes += bytes;
                UpdateNetworkDownloadState();
            }
        }

        public void MarkFileTransferred()
        {
            lock (_gate)
            {
                _transferredFiles = Math.Min(_totalFiles, _transferredFiles + 1);
            }
        }

        public DownloadGroupSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new DownloadGroupSnapshot(_server, _status, _transferredBytes, _totalBytes, _transferredFiles,
                    _totalFiles, _statusMessage);
            }
        }

        public void StopTrackingNetworkDownload()
        {
            lock (_gate)
            {
                if (_adjustIncompleteNetworkDownloadCount == null) return;
                if (_networkDownloadIncomplete)
                    _adjustIncompleteNetworkDownloadCount(-1);
                _adjustIncompleteNetworkDownloadCount = null;
            }
        }

        private bool IsNetworkDownloadIncomplete() => _totalBytes > 0 && _transferredBytes < _totalBytes;

        private void UpdateNetworkDownloadState()
        {
            var incomplete = IsNetworkDownloadIncomplete();
            if (incomplete == _networkDownloadIncomplete) return;

            _networkDownloadIncomplete = incomplete;
            _adjustIncompleteNetworkDownloadCount?.Invoke(incomplete ? 1 : -1);
        }
    }

    public sealed class DownloadStatusHandle : IDisposable
    {
        private readonly DownloadStatusStore _store;
        private readonly DownloadTracker _tracker;

        internal DownloadStatusHandle(DownloadStatusStore store, DownloadTracker tracker)
        {
            _store = store;
            _tracker = tracker;
        }

        public DownloadGroupHandle AddGroup(string server, long totalBytes, int totalFiles)
        {
            return new DownloadGroupHandle(_tracker.AddGroup(server, totalBytes, totalFiles));
        }

        public void Dispose()
        {
            _store.Remove(_tracker);
        }
    }

    public sealed class DownloadGroupHandle
    {
        private readonly DownloadGroupState _state;

        internal DownloadGroupHandle(DownloadGroupState state)
        {
            _state = state;
        }

        public void SetStatus(DownloadStatus status) => _state.SetStatus(status);

        public void SetUnavailable(string message) => _state.SetUnavailable(message);

        public void SetTotalBytes(long totalBytes) => _state.SetTotalBytes(totalBytes);

        public void AddBytes(long bytes) => _state.AddBytes(bytes);

        public void MarkFileTransferred() => _state.MarkFileTransferred();
    }
}
