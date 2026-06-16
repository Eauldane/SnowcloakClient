using Snowcloak.PlayerData.Handlers;
using Snowcloak.WebAPI.Files.Models;

namespace Snowcloak.WebAPI.Files;

public sealed class DownloadStatusStore
{
    private readonly Lock _gate = new();
    private readonly List<DownloadTracker> _active = [];

    public DownloadStatusHandle Begin(GameObjectHandler handler, string? uid)
    {
        var tracker = new DownloadTracker(handler, uid);
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
            _active.Remove(tracker);
        }
    }

    internal sealed class DownloadTracker
    {
        private readonly Lock _gate = new();
        private readonly List<DownloadGroupState> _groups = [];

        public DownloadTracker(GameObjectHandler handler, string? uid)
        {
            Handler = handler;
            Uid = uid;
        }

        public GameObjectHandler Handler { get; }
        public string? Uid { get; }

        public DownloadGroupState AddGroup(string server, long totalBytes, int totalFiles)
        {
            var state = new DownloadGroupState(server, totalBytes, totalFiles);
            lock (_gate)
            {
                _groups.Add(state);
            }

            return state;
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

        public DownloadGroupState(string server, long totalBytes, int totalFiles)
        {
            _server = server;
            _totalBytes = totalBytes;
            _totalFiles = totalFiles;
        }

        public void SetStatus(DownloadStatus status)
        {
            lock (_gate)
            {
                _status = status;
            }
        }

        public void SetTotalBytes(long totalBytes)
        {
            lock (_gate)
            {
                _totalBytes = totalBytes;
            }
        }

        public void AddBytes(long bytes)
        {
            lock (_gate)
            {
                _transferredBytes += bytes;
            }
        }

        public void MarkFileTransferred()
        {
            lock (_gate)
            {
                _transferredFiles = _totalFiles;
            }
        }

        public DownloadGroupSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new DownloadGroupSnapshot(_server, _status, _transferredBytes, _totalBytes, _transferredFiles, _totalFiles);
            }
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

        public void SetTotalBytes(long totalBytes) => _state.SetTotalBytes(totalBytes);

        public void AddBytes(long bytes) => _state.AddBytes(bytes);

        public void MarkFileTransferred() => _state.MarkFileTransferred();
    }
}
