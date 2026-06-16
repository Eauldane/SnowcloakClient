using Microsoft.Extensions.Hosting;

namespace Snowcloak.Configuration;

public sealed class StateDocumentWarmup : IHostedService
{
    private readonly IReadOnlyList<IStateDocument> _documents;

    public StateDocumentWarmup(IEnumerable<IStateDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents.ToArray();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var document in _documents)
        {
            _ = document.FileName;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
