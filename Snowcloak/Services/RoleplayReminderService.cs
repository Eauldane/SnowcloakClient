using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;

namespace Snowcloak.Services;

public sealed class RoleplayReminderService : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReminderWindow = TimeSpan.FromMinutes(30);
    private readonly ILogger<RoleplayReminderService> _logger;
    private readonly RoleplayClientService _roleplay;
    private readonly SnowcloakConfigService _config;
    private readonly IChatGui _chat;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<Guid, DateTime> _sent = [];
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
        var now = DateTime.UtcNow;
        var reminders = _config.Current.RpEventReminders;
        foreach (var entry in _roleplay.JoinedEvents.Concat(_roleplay.PublicEvents.Entries)
                     .GroupBy(item => item.Event.Id)
                     .Select(group => group.First()))
        {
            var item = entry.Event;
            if (!reminders.Contains(item.Id) || item.StartsAtUtc < now || item.StartsAtUtc - now > ReminderWindow) continue;
            if (_sent.TryGetValue(item.Id, out var sentStart) && sentStart == item.StartsAtUtc) continue;
            _sent[item.Id] = item.StartsAtUtc;
            _chat.Print(new XivChatEntry
            {
                Type = XivChatType.SystemMessage,
                Message = $"[Snowcloak] Event reminder: {item.Title} starts at {item.StartsAtUtc.ToLocalTime():g}.",
            });
        }
        foreach (var stale in _sent.Where(item => item.Value < now.AddDays(-2)).Select(item => item.Key).ToArray())
            _sent.Remove(stale);
    }
}
