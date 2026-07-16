namespace Snowcloak.Configuration.Models;

public enum ChatSoundOption
{
    None,
    Sound1,
    Sound2,
    Sound3,
    Sound4,
    Sound5,
    Sound6,
    Sound7,
    Sound8,
    Sound9,
    Sound10,
    Sound11,
    Sound12,
    Sound13,
    Sound14,
    Sound15,
    Sound16,
}

public sealed record ConversationPrefs
{
    public bool? Muted { get; set; }
    public ChatSoundOption? Sound { get; set; }
}
