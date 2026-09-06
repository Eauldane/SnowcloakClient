using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Dto.TemporaryAppearance;
using Snowcloak.API.Protocol;
using Snowcloak.Services.Mediator;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public TemporaryAppearanceDescriptor? TemporaryAppearanceDescriptor
        => _connectionContext.Dto?.TemporaryAppearance;

    public bool SupportsTemporaryAppearance => IsConnected
        && _connectionContext.Dto?.ServerCapabilities.HasFlag(HubCapability.TemporaryAppearanceV1) is true
        && TemporaryAppearanceDescriptor is not null;

    public async Task<TemporaryAppearanceCommandResult> TemporaryAppearanceUpdate(TemporaryAppearanceSourceCommand command)
        => await _snowHub!.InvokeAsync<TemporaryAppearanceCommandResult>(nameof(TemporaryAppearanceUpdate), command,
            _connectionLifecycle.ConnectionToken).ConfigureAwait(false);

    public async Task<TemporaryAppearanceSnapshot> TemporaryAppearanceGetSnapshot(TemporaryAppearanceSnapshotRequest request)
        => await _snowHub!.InvokeAsync<TemporaryAppearanceSnapshot>(nameof(TemporaryAppearanceGetSnapshot), request,
            _connectionLifecycle.ConnectionToken).ConfigureAwait(false);

    public async Task<TemporaryAppearanceSnapshot> TemporaryAppearanceSetPeerConsent(TemporaryAppearancePeerConsentCommand command)
        => await _snowHub!.InvokeAsync<TemporaryAppearanceSnapshot>(nameof(TemporaryAppearanceSetPeerConsent), command,
            _connectionLifecycle.ConnectionToken).ConfigureAwait(false);

    public Task Client_TemporaryAppearanceInvalidated(TemporaryAppearanceInvalidated invalidation)
    {
        var descriptor = TemporaryAppearanceDescriptor;
        if (descriptor == null
            || !invalidation.ServerEpoch.AsSpan().SequenceEqual(descriptor.ServerEpoch)
            || !invalidation.BindingId.AsSpan().SequenceEqual(descriptor.BindingId))
            return Task.CompletedTask;
        ExecuteSafely(() => Mediator.Publish(new TemporaryAppearanceInvalidatedMessage(invalidation)));
        return Task.CompletedTask;
    }
}
