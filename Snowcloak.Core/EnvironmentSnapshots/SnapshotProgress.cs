using System.Diagnostics;

namespace Snowcloak.Core.EnvironmentSnapshots;

public enum SnapshotPhase { Ready, Enumerating, Capturing, Submitting, Checking, Staging, Compressing, Uploading, Verifying, Finalizing, Complete, Failed, Cancelled, Preview, Downloading, Unload, Installing, Reload, Reconciling, RolledBack }

public sealed record SnapshotProgressView
{
    public SnapshotPhase Phase { get; init; }
    public string CurrentPath { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public int Attempt { get; init; }
    public int Files { get; init; }
    public long ContentBytes { get; init; }
    public int Objects { get; init; }
    public int ObjectsDone { get; init; }
    public int ReusedObjects { get; init; }
    public long ReusedBytes { get; init; }
    public int Uploads { get; init; }
    public int UploadedObjects { get; init; }
    public int PreparedUploads { get; init; }
    public long UploadBytes { get; init; }
    public bool UploadTotalKnown => Uploads > 0 && PreparedUploads == Uploads;
    public long SentBytes { get; init; }
    public long CurrentSent { get; init; }
    public long CurrentTotal { get; init; }
    public double BytesPerSecond { get; init; }
    public float PhaseFraction => Total > 0 ? Math.Clamp((float)Done / Total, 0, 1) : 0;
    public float ObjectFraction => Objects > 0 ? Math.Clamp((float)ObjectsDone / Objects, 0, 1) : Phase == SnapshotPhase.Complete ? 1 : 0;
    public float UploadFraction => Uploads > 0 ? Math.Clamp((float)UploadedObjects / Uploads, 0, 1) : 0;
    public float CurrentFraction => CurrentTotal > 0 ? Math.Clamp((float)CurrentSent / CurrentTotal, 0, 1) : 0;
}

public sealed class SnapshotProgress
{
    private readonly Lock _gate = new();
    private SnapshotProgressView _view = new();
    private long _sampleAt;
    private long _sampleBytes;
    public SnapshotProgressView Read() { lock (_gate) return _sampleAt != 0 && Stopwatch.GetElapsedTime(_sampleAt).TotalSeconds > 2 ? _view with { BytesPerSecond = 0 } : _view; }
    public void Reset() { lock (_gate) { _view = new(); _sampleAt = 0; _sampleBytes = 0; } }
    public void Phase(SnapshotPhase phase, int total = 0, string path = "", int attempt = 0)
    { lock (_gate) _view = _view with { Phase = phase, Done = 0, Total = Math.Max(0, total), CurrentPath = path, Attempt = attempt }; }
    public void Advance(int done, string? path = null)
    { lock (_gate) _view = _view with { Done = _view.Total > 0 ? Math.Clamp(done, 0, _view.Total) : Math.Max(0, done), CurrentPath = path ?? _view.CurrentPath }; }
    public void Inventory(int files, long bytes, int objects)
    { lock (_gate) _view = _view with { Files = files, ContentBytes = bytes, Objects = objects }; }
    public void Reuse(long bytes)
    { lock (_gate) _view = _view with { ReusedObjects = _view.ReusedObjects + 1, ReusedBytes = _view.ReusedBytes + bytes, ObjectsDone = _view.ObjectsDone + 1 }; }
    public void PlanUploads(int uploads, long rawBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawBytes);
        lock (_gate) _view = _view with { Uploads = uploads, UploadBytes = rawBytes };
    }
    public void PreparedUpload(long size, long rawSize = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfNegative(rawSize);
        lock (_gate)
        {
            if (_view.PreparedUploads >= _view.Uploads) throw new InvalidOperationException("No pending upload to prepare.");
            _view = _view with { PreparedUploads = _view.PreparedUploads + 1, UploadBytes = checked(_view.UploadBytes - rawSize + size) };
        }
    }
    public void StartUpload(string path, long size, SnapshotPhase phase = SnapshotPhase.Uploading)
    {
        lock (_gate)
        {
            _sampleAt = Stopwatch.GetTimestamp(); _sampleBytes = _view.SentBytes;
            _view = _view with { Phase = phase, CurrentPath = path, CurrentSent = 0, CurrentTotal = Math.Max(0, size), BytesPerSecond = 0 };
        }
    }
    public void Transfer(long sent, long size)
    {
        lock (_gate)
        {
            sent = size > 0 ? Math.Clamp(sent, _view.CurrentSent, Math.Max(_view.CurrentSent, size)) : Math.Max(sent, _view.CurrentSent);
            long total = _view.SentBytes + sent - _view.CurrentSent;
            var elapsed = Stopwatch.GetElapsedTime(_sampleAt).TotalSeconds;
            double rate = _view.BytesPerSecond;
            if (elapsed >= 0.2) { rate = (total - _sampleBytes) / elapsed; _sampleBytes = total; _sampleAt = Stopwatch.GetTimestamp(); }
            _view = _view with { SentBytes = total, CurrentSent = sent, CurrentTotal = Math.Max(0, size), BytesPerSecond = rate };
        }
    }
    public void EndTransfer()
    { lock (_gate) _view = _view with { CurrentSent = 0, CurrentTotal = 0, BytesPerSecond = 0 }; }
    public void Uploaded()
    { lock (_gate) _view = _view with { UploadedObjects = _view.UploadedObjects + 1, ObjectsDone = _view.ObjectsDone + 1, CurrentSent = 0, CurrentTotal = 0, BytesPerSecond = 0 }; }
}

public readonly record struct SnapshotCaptureProgress(int Done, int Total, int Attempt, string Path, SnapshotPhase Phase = SnapshotPhase.Capturing);
