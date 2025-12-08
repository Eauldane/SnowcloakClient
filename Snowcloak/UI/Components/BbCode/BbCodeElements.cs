namespace Snowcloak.UI.Components.BbCode;

using System.Numerics;
using Dalamud.Interface.ManagedFontAtlas;

public sealed record BbCodeStyle(
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    Vector4? Color = null,
    string? Url = null,
    bool UseTextAsUrl = false,
    float? FontScale = null,
    float? FontSize = null,
    BbCodeAlignment Alignment = BbCodeAlignment.Left)
{
    public BbCodeStyle WithUrl(string? url)
    {
        return this with { Url = url, UseTextAsUrl = UseTextAsUrl && string.IsNullOrWhiteSpace(url) };
    }
}


public enum BbCodeAlignment
{
    Left,
    Center,
    Right,
}

public abstract record BbCodeElement(BbCodeStyle Style);

public sealed record BbCodeTextElement(string Text, BbCodeStyle Style) : BbCodeElement(Style);

public sealed record BbCodeImageElement(string Source, BbCodeStyle Style) : BbCodeElement(Style);

public sealed record BbCodeEmoteElement(string EmoteName, BbCodeStyle Style) : BbCodeElement(Style);

public sealed record BbCodeNewLineElement() : BbCodeElement(new BbCodeStyle());

public sealed record BbCodeRenderOptions(
    float EmoteSize = 28f,
    float MaxImageWidth = 256f,
    float MaxImageHeight = 256f,
    bool AllowImages = true,
    bool AllowEmotes = true,
    bool AllowLinks = true,
    Action<string>? OnLinkClicked = null);