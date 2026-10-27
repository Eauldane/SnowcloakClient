using ElezenTools.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Sound;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration.Models;
using Snowcloak.Configuration;

namespace Snowcloak.Services.Chat;

public sealed class ChatSoundPlayer(ILogger<ChatSoundPlayer> logger, SnowcloakConfigService configService) : IDisposable
{
    private volatile bool _disposed;

    public void Play(ChatSoundOption sound)
    {
        if (_disposed || sound == ChatSoundOption.None || !Enum.IsDefined(sound) || configService.Current.ChatSoundVolume <= 0) return;
        _ = PlayOnFrameworkAsync(sound);
    }

    private async Task PlayOnFrameworkAsync(ChatSoundOption sound)
    {
        try
        {
            await Service.RunOnFrameworkAsync(() =>
            {
                var volume = Math.Clamp(configService.Current.ChatSoundVolume, 0, 100) / 100f;
                if (!_disposed && volume > 0) PlayGameSound(sound, volume);
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to play chat sound {Sound}", sound);
        }
    }

    private static unsafe void PlayGameSound(ChatSoundOption sound, float volume)
    {
        SoundData* padSound = null;
        SoundData* gameSound = null;
        // PlayChatSoundEffect maps choices 1..16 to UI effects 37..52.
        UIGlobals.PlaySoundEffect((uint)sound + 36, &padSound, &gameSound);
        if (gameSound != null) gameSound->SetVolume(gameSound->GetVolume() * volume);
        if (padSound != null && padSound != gameSound) padSound->SetVolume(padSound->GetVolume() * volume);
    }

    public void Dispose() => _disposed = true;
}
