namespace Snowcloak.Interop.Ipc;

public interface IIpcCaller : IDisposable
{
    bool APIAvailable { get; }
    void CheckAPI();
}
