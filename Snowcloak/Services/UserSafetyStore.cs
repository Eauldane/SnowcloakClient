using Snowcloak.API.Dto.User;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;

namespace Snowcloak.Services;

public sealed class UserSafetyStore : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly Lock _sync = new();
    private Task? _loadTask;
    private int _generation;

    public UserSafetyStore(ILogger<UserSafetyStore> logger, ApiController apiController, SnowMediator mediator)
        : base(logger, mediator)
    {
        _apiController = apiController;
        Mediator.Subscribe<ConnectedMessage>(this, _ => Reload());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => Reset());
        Mediator.Subscribe<ConnectionLostMessage>(this, _ => Reset());
    }

    public UserSafetyStateDto State { get; private set; } = new(false, null, []);
    public bool IsAvailable => _apiController.SupportsOpenRpSafety;
    public string Status { get; private set; } = string.Empty;
    public bool IsBusy { get; private set; }

    public void EnsureLoaded()
        => _ = GetOrStartLoad();

    public void Refresh() => _ = RefreshAsync();

    public Task RefreshAsync()
    {
        Reset();
        return GetOrStartLoad();
    }

    public void SetAdultContent(bool enabled)
    {
        Start(() => _apiController.UserSafetySetAdultContent(new AdultContentOptInDto(enabled)),
            enabled ? "Adult content is enabled for this UID." : "Adult content is disabled for this UID.");
    }

    public void Block(string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        Start(() => _apiController.UserBlock(new UserBlockRequestDto(uid)), $"Blocked {uid.Trim().ToUpperInvariant()}.");
    }

    public void Unblock(string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        Start(() => _apiController.UserUnblock(new UserBlockRequestDto(uid)), $"Unblocked {uid.Trim().ToUpperInvariant()}.");
    }

    private void Start(Func<Task<UserSafetyStateDto>> operation, string success)
    {
        lock (_sync)
        {
            if (IsBusy) return;
            _loadTask = RunAsync(operation, success, _generation);
        }
    }

    private async Task RunAsync(Func<Task<UserSafetyStateDto>> operation, string success, int generation)
    {
        IsBusy = true;
        Status = string.Empty;
        try
        {
            var state = await operation().ConfigureAwait(false);
            lock (_sync)
            {
                if (generation != _generation) return;
                State = state;
                Status = success;
            }
            Mediator.Publish(new ClearCharacterProfileDataMessage());
            Mediator.Publish(new OpenRpSafetyChangedMessage(state));
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (generation == _generation)
                    Status = ex.Message;
            }
        }
        finally
        {
            lock (_sync)
            {
                if (generation == _generation)
                    IsBusy = false;
            }
        }
    }

    private void Reload()
    {
        Reset();
        EnsureLoaded();
    }

    private Task GetOrStartLoad()
    {
        lock (_sync)
        {
            if (!IsAvailable)
            {
                _loadTask = null;
                return Task.CompletedTask;
            }

            return _loadTask ??= RunAsync(_apiController.UserSafetyGet, string.Empty, _generation);
        }
    }

    private void Reset()
    {
        lock (_sync)
        {
            _generation++;
            _loadTask = null;
            State = new UserSafetyStateDto(false, null, []);
            Status = string.Empty;
            IsBusy = false;
        }
    }
}
