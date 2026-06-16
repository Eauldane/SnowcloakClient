using Snowcloak.Core.Authentication;

namespace Snowcloak.WebAPI.SignalR;

public sealed class SnowAuthFailureException : Exception
{
    public SnowAuthFailureException()
        : this(string.Empty)
    {
    }

    public SnowAuthFailureException(string reason)
        : base(reason)
    {
        Reason = reason;
        Kind = AuthenticationFailureClassifier.Classify(reason);
    }

    public SnowAuthFailureException(string reason, Exception innerException)
        : base(reason, innerException)
    {
        Reason = reason;
        Kind = AuthenticationFailureClassifier.Classify(reason);
    }

    public string Reason { get; }

    public AuthenticationFailureKind Kind { get; }

    public bool IsTransient => AuthenticationFailureClassifier.IsTransient(Kind);
}
