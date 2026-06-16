namespace Snowcloak.Initialization;

/// <summary>
/// Declares which runtime-scoped services are eagerly constructed when the runtime scope
/// is created after login, and in what order.
/// </summary>
public sealed class RuntimeServicePlan
{
    public required IReadOnlyList<Type> BaseServices { get; init; }
    public required IReadOnlyList<Type> ConfiguredServices { get; init; }
}
