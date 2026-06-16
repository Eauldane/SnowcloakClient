using Snowcloak.Core.Scheduling;

namespace Snowcloak.Game.Scheduling;

public interface IFrameScheduler
{
    double BudgetMs { get; set; }

    IFrameTickHandle Register(string name, TickInterval interval, TickPriority priority, Action tick, params string[] pauseGates);

    IFrameTickHandle RegisterGated(string name, TickInterval interval, TickPriority priority, Action tick, IReadOnlyList<string> pauseGates, IReadOnlyList<string> runOnlyGates);

    void ActivateGate(string gate, string reason);

    void DeactivateGate(string gate, string reason);
}
