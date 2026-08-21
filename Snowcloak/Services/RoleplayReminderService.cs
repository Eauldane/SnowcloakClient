using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;

namespace Snowcloak.Services;

public sealed class RoleplayReminderService : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private readonly ILogger<RoleplayReminderService> _logger;
    private readonly RoleplayClientService _roleplay;
    private readonly SnowcloakConfigService _config;
    private readonly IChatGui _chat;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<ReminderKey, long> _sent = [];
    private Task? _runTask;
    private DateTimeOffset _lastRefresh;

    public RoleplayReminderService(ILogger<RoleplayReminderService> logger, RoleplayClientService roleplay,
        SnowcloakConfigService config, IChatGui chat)
    {
        _logger = logger;
        _roleplay = roleplay;
        _config = config;
        _chat = chat;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runTask = Task.Run(RunAsync, _cancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                _logger.LogTrace("Roleplay reminder service stopped");
            }
        }
    }

    public void Dispose() => _cancellation.Dispose();

    private async Task RunAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - _lastRefresh >= TimeSpan.FromMinutes(10))
                {
                    await _roleplay.RefreshAsync().ConfigureAwait(false);
                    _lastRefresh = DateTimeOffset.UtcNow;
                }
                CheckReminders();
                await Task.Delay(PollInterval, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check roleplay event reminders");
                await Task.Delay(PollInterval, _cancellation.Token).ConfigureAwait(false);
            }
        }
    }

    private void CheckReminders()
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromMinutes(Math.Clamp(_config.Current.RpReminderWindowMinutes, 5, 1440));
        var reminders = _config.Current.RpEventReminders;
        foreach (var entry in _roleplay.JoinedEvents.Concat(_roleplay.PublicEvents.Entries)
                     .GroupBy(item => item.Event.Id)
                     .Select(group => group.First()))
        {
            var item = entry.Event;
            var startsAt = new DateTimeOffset(DateTime.SpecifyKind(item.StartsAtUtc, DateTimeKind.Utc));
            if (!reminders.Contains(item.Id)) continue;
            TrySendReminder(new ReminderKey(ReminderKind.Event, item.Id.ToString("D")), startsAt, now, window,
                $"Event reminder: {item.Title} starts at {startsAt.ToLocalTime():g}.");
        }

        var availability = _roleplay.OwnAvailability;
        if (_config.Current.RemindAvailabilityExpiry && availability is { Paused: false })
        {
            TrySendReminder(new ReminderKey(ReminderKind.Availability, "own"), availability.ExpiresAtUtc, now, window,
                $"Your RP availability expires in {FormatRemaining(availability.ExpiresAtUtc - now)} - refresh it to stay listed.");
        }

        if (_config.Current.RemindHookExpiry)
        {
            foreach (var hook in _roleplay.CurrentHooks.Hooks)
            {
                TrySendReminder(new ReminderKey(ReminderKind.Hook, hook.HookId), hook.ExpiresAtUtc, now, window,
                    $"Your RP hook '{hook.Title}' expires in {FormatRemaining(hook.ExpiresAtUtc - now)}.");
            }
        }

        var staleEpoch = now.AddDays(-2).ToUnixTimeSeconds();
        foreach (var stale in _sent.Where(item => item.Value < staleEpoch).Select(item => item.Key).ToArray())
            _sent.Remove(stale);
    }

    private void TrySendReminder(ReminderKey key, DateTimeOffset dueAt, DateTimeOffset now, TimeSpan window, string message)
    {
        if (dueAt < now || dueAt - now > window) return;
        var epoch = dueAt.ToUnixTimeSeconds();
        if (_sent.TryGetValue(key, out var sentEpoch) && sentEpoch == epoch) return;
        _sent[key] = epoch;
        _chat.Print(new XivChatEntry
        {
            Type = XivChatType.SystemMessage,
            Message = "[Snowcloak] " + message,
        });
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return minutes == 1 ? "about 1 minute" : $"about {minutes} minutes";
    }

    private enum ReminderKind
    {
        Event,
        Availability,
        Hook,
    }

    private readonly record struct ReminderKey(ReminderKind Kind, string Id);
}
