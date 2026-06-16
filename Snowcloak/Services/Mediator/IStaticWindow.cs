namespace Snowcloak.Services.Mediator;

/// <summary>
/// Marks a WindowMediatorSubscriberBase window that is managed by the DI container
/// and registered into the window collection by assembly scanning
///
/// Windows that are created on demand by a factory (with runtime constructor arguments such as
/// a specific pair) must NOT implement this
/// </summary>
public interface IStaticWindow
{
}
