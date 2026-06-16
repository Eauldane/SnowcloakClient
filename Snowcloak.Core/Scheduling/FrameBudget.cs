namespace Snowcloak.Core.Scheduling;

public sealed class FrameBudget
{
    private double _budgetMs;
    private double _spentMs;

    public FrameBudget(double budgetMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetMs);
        _budgetMs = budgetMs;
    }

    public double BudgetMs
    {
        get => _budgetMs;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _budgetMs = value;
        }
    }

    public double SpentMs => _spentMs;

    public bool IsExhausted => _spentMs >= _budgetMs;

    public void Reset() => _spentMs = 0;

    public void Record(double elapsedMs) => _spentMs += elapsedMs;

    public bool ShouldRun(TickPriority priority) => priority == TickPriority.Critical || _spentMs < _budgetMs;
}
