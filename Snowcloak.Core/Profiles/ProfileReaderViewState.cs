using Snowcloak.API.Dto.User;

namespace Snowcloak.Core.Profiles;

public sealed class ProfileReaderViewState
{
    private string? _profile;
    private bool _classic;

    public void Open(string profile)
    {
        if (_profile == profile) return;
        _profile = profile;
        _classic = false;
    }

    public void Reset() { _profile = null; _classic = false; }
    public static bool CanSwitch(ProfileBuilderDto? builder, bool traditionalOnly)
        => !traditionalOnly && builder?.Presentation == ProfilePresentation.Custom;
    public bool UsesClassic(ProfileBuilderDto? builder, bool traditionalOnly)
        => _classic || ProfilePresentationPolicy.UsesTraditional(builder, traditionalOnly);
    public void Toggle(ProfileBuilderDto? builder, bool traditionalOnly)
    {
        if (CanSwitch(builder, traditionalOnly)) _classic = !_classic;
    }
}
