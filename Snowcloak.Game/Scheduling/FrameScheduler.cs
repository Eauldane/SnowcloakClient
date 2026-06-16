using System.Collections.Concurrent;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Core.Scheduling;

namespace Snowcloak.Game.Scheduling;

public sealed class FrameScheduler : IFrameScheduler, IHostedService
{
    public const double DefaultBudgetMs = 2.0;

    private static readonly string[] NoGates = [];

    private readonly ILogger<FrameScheduler> _logger;
    private readonly IFramework _framework;
    private readonly IFrameTickProfiler _profiler;

    private readonly Lock _gate = new();
    private readonly FrameSchedulePlanner _planner = new();
    private readonly FrameBudget _budget = new(DefaultBudgetMs);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly ConcurrentDictionary<int, Registration> _registrations = new();
    private readonly Dictionary<string, HashSet<string>> _gateReasons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _gateMembers = new(StringComparer.Ordinal);

    private readonly List<DueTicker> _frameDue = [];
    private readonly List<int> _frameRan = [];

    private long _frame;

    private sealed record Registration(string Name, Action Tick, string[] PauseGates, string[] RunOnlyGates);

    public FrameScheduler(ILogger<FrameScheduler> logger, IFramework framework, IFrameTickProfiler profiler)
    {
        _logger = logger;
        _framework = framework;
        _profiler = profiler;
    }

    public double BudgetMs
    {
        get { lock (_gate) return _budget.BudgetMs; }
        set { lock (_gate) _budget.BudgetMs = value; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting FrameScheduler");
        _framework.Update += OnFrameworkUpdate;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _framework.Update -= OnFrameworkUpdate;
        return Task.CompletedTask;
    }

    public IFrameTickHandle Register(string name, TickInterval interval, TickPriority priority, Action tick, params string[] pauseGates)
        => RegisterCore(name, interval, priority, tick, pauseGates ?? NoGates, NoGates);

    public IFrameTickHandle RegisterGated(string name, TickInterval interval, TickPriority priority, Action tick, IReadOnlyList<string> pauseGates, IReadOnlyList<string> runOnlyGates)
        => RegisterCore(name, interval, priority, tick, pauseGates?.ToArray() ?? NoGates, runOnlyGates?.ToArray() ?? NoGates);

    private IFrameTickHandle RegisterCore(string name, TickInterval interval, TickPriority priority, Action tick, string[] pauseGates, string[] runOnlyGates)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(tick);

        int id;
        lock (_gate)
        {
            id = _planner.Register(interval, priority);
            _registrations[id] = new Registration(name, tick, pauseGates, runOnlyGates);
            foreach (var gate in EnumerateGates(pauseGates, runOnlyGates))
            {
                if (!_gateMembers.TryGetValue(gate, out var members))
                    _gateMembers[gate] = members = [];
                members.Add(id);
            }

            RefreshPaused(id);
        }

        return new Handle(this, id, name);
    }

    public void ActivateGate(string gate, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(gate);
        ArgumentException.ThrowIfNullOrEmpty(reason);

        lock (_gate)
        {
            if (!_gateReasons.TryGetValue(gate, out var reasons))
                _gateReasons[gate] = reasons = new HashSet<string>(StringComparer.Ordinal);
            if (reasons.Add(reason))
                RefreshGateMembers(gate);
        }
    }

    public void DeactivateGate(string gate, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(gate);
        ArgumentException.ThrowIfNullOrEmpty(reason);

        lock (_gate)
        {
            if (_gateReasons.TryGetValue(gate, out var reasons) && reasons.Remove(reason))
                RefreshGateMembers(gate);
        }
    }

    private void OnFrameworkUpdate(IFramework framework) => _profiler.Run("Tick", RunFrame);

    private void RunFrame()
    {
        var frame = unchecked(++_frame);
        var nowMs = _clock.Elapsed.TotalMilliseconds;

        _frameDue.Clear();
        lock (_gate)
        {
            _budget.Reset();
            _frameDue.AddRange(_planner.CollectDue(frame, nowMs));
        }

        _frameRan.Clear();
        foreach (var due in _frameDue)
        {
            bool shouldRun;
            lock (_gate)
                shouldRun = _budget.ShouldRun(due.Priority);
            if (!shouldRun)
                continue;

            if (!_registrations.TryGetValue(due.Id, out var registration))
                continue;

            var startMs = _clock.Elapsed.TotalMilliseconds;
            try
            {
                _profiler.Run(registration.Name, registration.Tick);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Frame ticker {name} faulted; isolating it so the frame loop and other tickers keep running", registration.Name);
            }

            var elapsedMs = _clock.Elapsed.TotalMilliseconds - startMs;
            lock (_gate)
                _budget.Record(elapsedMs);
            _frameRan.Add(due.Id);
        }

        if (_frameRan.Count > 0)
        {
            lock (_gate)
            {
                foreach (var id in _frameRan)
                    _planner.MarkRan(id, frame, nowMs);
            }
        }
    }

    private void Unregister(int id)
    {
        lock (_gate)
        {
            if (_registrations.TryRemove(id, out var registration))
            {
                foreach (var gate in EnumerateGates(registration.PauseGates, registration.RunOnlyGates))
                {
                    if (_gateMembers.TryGetValue(gate, out var members))
                        members.Remove(id);
                }
            }

            _planner.Unregister(id);
        }
    }

    private void RefreshGateMembers(string gate)
    {
        if (!_gateMembers.TryGetValue(gate, out var members))
            return;
        foreach (var id in members)
            RefreshPaused(id);
    }

    private void RefreshPaused(int id)
    {
        if (!_registrations.TryGetValue(id, out var registration))
            return;

        var paused = false;
        foreach (var gate in registration.PauseGates)
        {
            if (IsGateActive(gate))
            {
                paused = true;
                break;
            }
        }

        if (!paused)
        {
            foreach (var gate in registration.RunOnlyGates)
            {
                if (!IsGateActive(gate))
                {
                    paused = true;
                    break;
                }
            }
        }

        _planner.SetPaused(id, paused);
    }

    private bool IsGateActive(string gate) => _gateReasons.TryGetValue(gate, out var reasons) && reasons.Count > 0;

    private static IEnumerable<string> EnumerateGates(string[] pauseGates, string[] runOnlyGates)
    {
        foreach (var gate in pauseGates)
            yield return gate;
        foreach (var gate in runOnlyGates)
            yield return gate;
    }

    private sealed class Handle : IFrameTickHandle
    {
        private FrameScheduler? _scheduler;
        private readonly int _id;

        public Handle(FrameScheduler scheduler, int id, string name)
        {
            _scheduler = scheduler;
            _id = id;
            Name = name;
        }

        public string Name { get; }

        public void Dispose()
        {
            var scheduler = Interlocked.Exchange(ref _scheduler, null);
            scheduler?.Unregister(_id);
        }
    }
}
