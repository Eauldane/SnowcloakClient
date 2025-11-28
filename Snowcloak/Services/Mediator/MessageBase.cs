namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048
public abstract record MessageBase
{
    public virtual bool KeepThreadContext => false;
    public virtual string? SubscriberKey => null;
}

public record SameThreadMessage : MessageBase
{
    public override bool KeepThreadContext => true;
}

public record KeyedMessage(string MessageKey, bool SameThread = false) : MessageBase
{
    public override string? SubscriberKey => MessageKey;
    public override bool KeepThreadContext => SameThread;
}
#pragma warning restore MA0048