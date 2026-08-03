using Snowcloak.Core.BbCode;

namespace Snowcloak.UI.Components;

internal static class ProfileBbCodeRenderOptions
{
    public static BbCodeRenderOptions Compact { get; } = new(
        AllowImages: false,
        AllowEmotes: false,
        ShowDisabledMediaAsText: false);

    public static BbCodeRenderOptions LongForm { get; } = new();
}
