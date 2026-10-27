using Snowcloak.API.Data;
using Snowcloak.PlayerData.Data;
using CharacterData = Snowcloak.API.Data.CharacterData;

namespace Snowcloak.PlayerData.Services;

internal sealed class CharacterDataPublicationQueue : IDisposable
{
    internal sealed record Publication(CharacterData Data, IReadOnlyDictionary<string, string> SourcePathsByHash,
        List<UserData> Recipients, long Revision);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly Func<bool> _isConnected;
    private readonly Func<Publication, CancellationToken, Task<bool>> _publish;
    private Publication? _pending;
    private long _revision;

    public CharacterDataPublicationQueue(Func<bool> isConnected, Func<Publication, CancellationToken, Task<bool>> publish)
    {
        _isConnected = isConnected;
        _publish = publish;
    }

    public void Queue(CharacterData data, IReadOnlyDictionary<string, string> sourcePathsByHash, List<UserData> recipients)
    {
        lock (_gate)
        {
            var pendingRecipients = (_pending?.Recipients ?? []).Concat(recipients)
                .DistinctBy(user => user.UID, StringComparer.Ordinal).ToList();
            _pending = new(data.Clone(), new Dictionary<string, string>(sourcePathsByHash, StringComparer.Ordinal),
                pendingRecipients, ++_revision);
            if (_changed.CurrentCount == 0) _changed.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            var failures = 0;
            while (_isConnected())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Publication? publication;
                lock (_gate) publication = _pending;
                if (publication == null) break;

                var succeeded = await _publish(publication, cancellationToken).ConfigureAwait(false);
                bool superseded;
                lock (_gate)
                {
                    superseded = _pending?.Revision != publication.Revision;
                    if (succeeded && !superseded) _pending = null;
                    if (_pending == null) break;
                }

                if (succeeded || superseded)
                {
                    failures = 0;
                    continue;
                }

                // Cap the delay, not the number of attempts. Transient failures must
                // recover even when the user makes no further appearance changes.
                var delay = TimeSpan.FromSeconds(++failures switch { 1 => 2, 2 => 5, 3 => 10, _ => 30 });
                await _changed.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _changed.Dispose();
}
