using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Dto.FileRepair;
using Snowcloak.API.Protocol;
namespace Snowcloak.WebAPI;
public partial class ApiController
{
    public bool SupportsFileRepair => IsConnected && _connectionContext.Dto?.ServerCapabilities.HasFlag(HubCapability.FileRepairV1) == true;
    public event Func<FileRepairProbe[], Task>? FileRepairProbeReceived;
    public event Func<FileRepairAssignment, Task>? FileRepairAssignmentReceived;
    public event Action<FileRepairStatus>? FileRepairStatusReceived;
    public Task Client_FileRepairProbe(FileRepairProbe[] probes) => FileRepairProbeReceived?.Invoke(probes) ?? Task.CompletedTask;
    public Task Client_FileRepairAssignment(FileRepairAssignment assignment) => FileRepairAssignmentReceived?.Invoke(assignment) ?? Task.CompletedTask;
    public Task Client_FileRepairStatus(FileRepairStatus status) { FileRepairStatusReceived?.Invoke(status); return Task.CompletedTask; }
    public Task FileRepairRegister(bool enabled, CancellationToken ct) => _snowHub!.InvokeAsync("FileRepairRegister", enabled, ct);
    public Task<List<FileRepairStatus>> FileRepairStart(FileRepairRequest request, CancellationToken ct) => _snowHub!.InvokeAsync<List<FileRepairStatus>>("FileRepairStart", request, ct);
    public Task<List<FileRepairStatus>> FileRepairGetStatus(Guid operation, string[] hashes, CancellationToken ct) => _snowHub!.InvokeAsync<List<FileRepairStatus>>("FileRepairStatus", operation, hashes, ct);
    public Task FileRepairHeartbeat(Guid operation, CancellationToken ct) => _snowHub!.InvokeAsync("FileRepairHeartbeat", operation, ct);
    public Task FileRepairCancel(Guid operation, CancellationToken ct) => _snowHub!.InvokeAsync("FileRepairCancel", operation, ct);
    public Task FileRepairReply(List<FileRepairOffer> offers, CancellationToken ct) => _snowHub!.InvokeAsync("FileRepairOffer", offers, ct);
    public Task FileRepairProgress(FileRepairAssignment assignment, long bytes, bool failed, CancellationToken ct) => _snowHub!.InvokeAsync("FileRepairProgress", assignment, bytes, failed, ct);

    Task Snowcloak.API.SignalR.IFileRepairApi.FileRepairRegister(bool enabled) => FileRepairRegister(enabled, _connectionLifecycle.ConnectionToken);
    Task<List<FileRepairStatus>> Snowcloak.API.SignalR.IFileRepairApi.FileRepairStart(FileRepairRequest request) => FileRepairStart(request, _connectionLifecycle.ConnectionToken);
    Task<List<FileRepairStatus>> Snowcloak.API.SignalR.IFileRepairApi.FileRepairStatus(Guid operation, string[] hashes) => FileRepairGetStatus(operation, hashes, _connectionLifecycle.ConnectionToken);
    Task Snowcloak.API.SignalR.IFileRepairApi.FileRepairHeartbeat(Guid operation) => FileRepairHeartbeat(operation, _connectionLifecycle.ConnectionToken);
    Task Snowcloak.API.SignalR.IFileRepairApi.FileRepairCancel(Guid operation) => FileRepairCancel(operation, _connectionLifecycle.ConnectionToken);
    Task Snowcloak.API.SignalR.IFileRepairApi.FileRepairOffer(List<FileRepairOffer> offers) => FileRepairReply(offers, _connectionLifecycle.ConnectionToken);
    Task Snowcloak.API.SignalR.IFileRepairApi.FileRepairProgress(FileRepairAssignment assignment, long bytes, bool failed) => FileRepairProgress(assignment, bytes, failed, _connectionLifecycle.ConnectionToken);
}
