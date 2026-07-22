namespace Snowcloak.Initialization;

public sealed class RuntimeServicePlan
{
    public required IReadOnlyList<Type> BaseServices { get; init; }
    public required IReadOnlyList<Type> ConfiguredServices { get; init; }
}
