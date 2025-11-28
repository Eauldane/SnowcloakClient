namespace Snowcloak.WebAPI.SignalR;

public class SnowAuthFailureException : Exception
{
    public SnowAuthFailureException(string reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}