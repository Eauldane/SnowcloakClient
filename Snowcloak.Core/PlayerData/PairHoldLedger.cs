using System.Collections.Concurrent;

namespace Snowcloak.Core.PlayerData;

public sealed class PairHoldLedger
{
    public const string AutoPauseVramReason = "AutoPause-VRAM";
    public const string AutoPauseTriangleReason = "AutoPause-Triangles";
    public const string AutoPauseCrowdPriorityReason = "AutoPause-CrowdPriority";

    // Download locks apply earlier in the process than Application locks.
    private readonly ConcurrentDictionary<string, int> _holdDownloadLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _holdApplicationLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AutoPauseReason, string> _autoPauseReasons = new();
    private volatile bool _autoPauseNotificationShown;

    public bool IsDownloadBlocked => _holdDownloadLocks.Any(f => f.Value > 0);
    public bool IsApplicationBlocked => _holdApplicationLocks.Any(f => f.Value > 0) || IsDownloadBlocked;

    public bool IsAutoPaused => _holdDownloadLocks.ContainsKey(AutoPauseVramReason)
        || _holdDownloadLocks.ContainsKey(AutoPauseTriangleReason)
        || _holdDownloadLocks.ContainsKey(AutoPauseCrowdPriorityReason);
    public bool IsCrowdPriorityAutoPaused => _holdDownloadLocks.ContainsKey(AutoPauseCrowdPriorityReason);

    public IEnumerable<string> AutoPauseReasons => _autoPauseReasons.Values;
    public IEnumerable<string> HoldDownloadReasons => _holdDownloadLocks.Keys;
    public IEnumerable<string> HoldApplicationReasons => Enumerable.Concat(_holdDownloadLocks.Keys, _holdApplicationLocks.Keys);
    public string? AutoPauseTooltip => _autoPauseReasons.IsEmpty ? null : string.Join(Environment.NewLine, _autoPauseReasons.Values);

    public bool AutoPauseNotificationShown => _autoPauseNotificationShown;
    public void MarkAutoPauseNotificationShown() => _autoPauseNotificationShown = true;

    public bool HoldApplication(string source, int maxValue = int.MaxValue) => Hold(_holdApplicationLocks, source, maxValue);

    public bool UnholdApplication(string source) => Unhold(_holdApplicationLocks, source);

    public bool HoldDownloads(string source, int maxValue = int.MaxValue) => Hold(_holdDownloadLocks, source, maxValue);

    public bool UnholdDownloads(string source) => Unhold(_holdDownloadLocks, source);

    public bool SetAutoPause(AutoPauseReason reason, string tooltip)
    {
        bool becameBlocked = Hold(_holdDownloadLocks, ReasonKey(reason), maxValue: 1);
        _autoPauseReasons[reason] = tooltip;
        return becameBlocked;
    }
    
    public void ClearAutoPause(AutoPauseReason? reason = null)
    {
        if (reason is null or AutoPauseReason.Vram)
        {
            _autoPauseReasons.TryRemove(AutoPauseReason.Vram, out _);
            Unhold(_holdDownloadLocks, AutoPauseVramReason);
        }

        if (reason is null or AutoPauseReason.Triangles)
        {
            _autoPauseReasons.TryRemove(AutoPauseReason.Triangles, out _);
            Unhold(_holdDownloadLocks, AutoPauseTriangleReason);
        }

        if (reason is null or AutoPauseReason.CrowdPriority)
        {
            _autoPauseReasons.TryRemove(AutoPauseReason.CrowdPriority, out _);
            Unhold(_holdDownloadLocks, AutoPauseCrowdPriorityReason);
        }

        if (!IsAutoPaused)
        {
            _autoPauseNotificationShown = false;
            _autoPauseReasons.Clear();
        }
    }

    public bool HasAutoPauseReason(AutoPauseReason reason) => _holdDownloadLocks.ContainsKey(ReasonKey(reason));

    public bool HasBlockingReasonsOtherThanCrowdPriority()
    {
        return _holdApplicationLocks.Any(f => f.Value > 0)
            || _holdDownloadLocks.Any(f => f.Value > 0 && !string.Equals(f.Key, AutoPauseCrowdPriorityReason, StringComparison.Ordinal));
    }

    private bool Hold(ConcurrentDictionary<string, int> table, string source, int maxValue)
    {
        bool wasBlocked = IsApplicationBlocked;
        table.AddOrUpdate(source, 1, (_, v) => Math.Min(maxValue, v + 1));
        // Adding any hold makes the pair blocked, so it transitioned iff it was not blocked before.
        return !wasBlocked;
    }

    private bool Unhold(ConcurrentDictionary<string, int> table, string source)
    {
        bool wasBlocked = IsApplicationBlocked;
        table.AddOrUpdate(source, 0, (_, v) => Math.Max(0, v - 1));
        table.TryRemove(new KeyValuePair<string, int>(source, 0));
        return wasBlocked && !IsApplicationBlocked;
    }

    private static string ReasonKey(AutoPauseReason reason) => reason switch
    {
        AutoPauseReason.Vram => AutoPauseVramReason,
        AutoPauseReason.Triangles => AutoPauseTriangleReason,
        AutoPauseReason.CrowdPriority => AutoPauseCrowdPriorityReason,
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
