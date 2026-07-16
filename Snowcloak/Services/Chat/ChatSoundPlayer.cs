using NAudio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;

namespace Snowcloak.Services.Chat;

public sealed class ChatSoundPlayer : IDisposable
{
    private static readonly Action<ILogger, ChatSoundOption, Exception?> PlaybackFailed =
        LoggerMessage.Define<ChatSoundOption>(LogLevel.Debug, new EventId(1, nameof(PlaybackFailed)),
            "Unable to play chat sound {Sound}");
    private readonly Lock _lock = new();
    private readonly HashSet<WaveOutEvent> _active = [];
    private readonly ILogger<ChatSoundPlayer> _logger;

    public ChatSoundPlayer(ILogger<ChatSoundPlayer> logger)
    {
        _logger = logger;
    }

    public void Play(ChatSoundOption sound)
    {
        if (sound == ChatSoundOption.None)
        {
            return;
        }

        var index = (int)sound;
        var source = new SignalGenerator
        {
            Gain = 0.07,
            Frequency = 420 + index * 32,
            Type = index % 3 == 0 ? SignalGeneratorType.Triangle : SignalGeneratorType.Sin,
        }.Take(TimeSpan.FromMilliseconds(100 + index * 4));
        WaveOutEvent? output = null;
        try
        {
            output = new WaveOutEvent { DesiredLatency = 80 };
            output.PlaybackStopped += OnPlaybackStopped;
            lock (_lock)
            {
                _active.Add(output);
            }

            output.Init(source);
            output.Play();
        }
        catch (MmException ex)
        {
            HandlePlaybackFailure(output, sound, ex);
        }
        catch (InvalidOperationException ex)
        {
            HandlePlaybackFailure(output, sound, ex);
        }
        catch (ArgumentException ex)
        {
            HandlePlaybackFailure(output, sound, ex);
        }
    }

    public void Dispose()
    {
        WaveOutEvent[] active;
        lock (_lock)
        {
            active = _active.ToArray();
            _active.Clear();
        }

        foreach (var output in active)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Dispose();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (sender is not WaveOutEvent output)
        {
            return;
        }

        output.PlaybackStopped -= OnPlaybackStopped;
        lock (_lock)
        {
            _active.Remove(output);
        }

        output.Dispose();
    }

    private void HandlePlaybackFailure(WaveOutEvent? output, ChatSoundOption sound, Exception exception)
    {
        if (output != null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            lock (_lock)
            {
                _active.Remove(output);
            }

            output.Dispose();
        }

        PlaybackFailed(_logger, sound, exception);
    }
}
