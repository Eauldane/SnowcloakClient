namespace Snowcloak.UI.Components.BbCode;

using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Dalamud.Utility;
using ElezenTools.UI;
using Snowcloak.Core.BbCode;
using System.Linq;

public sealed partial class BbCodeRenderer
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly TextureService _textureService;
    private readonly Dictionary<string, string> _emoteMappings;
    private readonly Dictionary<string, string> _emoteAliases;
    private readonly Dictionary<string, IReadOnlyList<BbCodeElement>> _parseCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _parseCacheOrder = new();

    private static readonly string[] EmoteExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];
    private static readonly string[] AllowedLinkSchemes = [Uri.UriSchemeHttp, Uri.UriSchemeHttps, Uri.UriSchemeMailto];
    private const int MaxParseCacheEntries = 64;

    public BbCodeRenderer(IDalamudPluginInterface pluginInterface, TextureService textureService)
    {
        _pluginInterface = pluginInterface;
        _textureService = textureService;

        _emoteMappings = new(StringComparer.OrdinalIgnoreCase);
        _emoteAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["at_left"] = "auto_translate_left",
            ["at_right"] = "auto_translate_right",
        };
        
        LoadEmoteMappings();
    }

    public IReadOnlyDictionary<string, string> EmoteMappings => _emoteMappings;

    public IReadOnlyList<BbCodeElement> Parse(string text)
    {
        if (_parseCache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        var parsed = BbCodeParser.Parse(text, _emoteMappings.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));

        _parseCache[text] = parsed;
        _parseCacheOrder.Enqueue(text);
        if (_parseCacheOrder.Count > MaxParseCacheEntries)
        {
            _parseCache.Remove(_parseCacheOrder.Dequeue());
        }

        return parsed;
    }

    public void Render(string text, float wrapWidth, BbCodeRenderOptions? options = null)
    {
        Render(Parse(text), wrapWidth, options);
    }

    public void Render(IReadOnlyList<BbCodeElement> elements, float wrapWidth, BbCodeRenderOptions? options = null)
    {
        var renderOptions = options ?? new BbCodeRenderOptions();
        var width = wrapWidth > 0 ? wrapWidth : ImGui.GetContentRegionAvail().X;
        var segments = BuildSegments(elements, renderOptions).ToList();
        var lines = BuildLines(segments, width);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var startPos = ImGui.GetCursorPos();
            var offset = CalculateAlignmentOffset(line, width);

            if (offset > 0f)
            {
                ImGui.SetCursorPosX(startPos.X + offset);
            }

            var firstOnLine = true;
            foreach (var segment in line.Segments)
            {
                if (!firstOnLine)
                {
                    ImGui.SameLine(0, 0);
                }

                RenderSegment(segment, renderOptions);
                firstOnLine = false;
            }

            var nextLinePos = new Vector2(startPos.X, startPos.Y + line.Height);
            ImGui.SetCursorPos(nextLinePos);
        }
    }

    private IEnumerable<LayoutSegment> BuildSegments(IReadOnlyList<BbCodeElement> elements, BbCodeRenderOptions options)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case BbCodeNewLineElement:
                    yield return LayoutSegment.NewLine;
                    break;
                case BbCodeTextElement textElement:
                    foreach (var part in SplitText(textElement.Text))
                    {
                        if (string.Equals(part, "\n", StringComparison.Ordinal))
                        {
                            yield return LayoutSegment.NewLine;
                            continue;
                        }

                        if (string.IsNullOrEmpty(part)) continue;
                        var size = CalculateTextSize(part, textElement.Style, options);
                        yield return LayoutSegment.FromText(part, size, textElement.Style);
                    }

                    break;
                case BbCodeEmoteElement emoteElement when options.AllowEmotes:
                    var emoteTexture = GetTextureForEmote(emoteElement.EmoteName);
                    var emoteSize = GetScaledImageSize(emoteTexture, options.EmoteSize, options.EmoteSize);
                    yield return LayoutSegment.FromImage(emoteSize, emoteElement.Style with { Underline = false }, emoteTexture);
                    break;
                case BbCodeEmoteElement emoteElement:
                    var emoteText = $":{emoteElement.EmoteName}:";
                    var emoteTextSize = ImGui.CalcTextSize(emoteText, hideTextAfterDoubleHash: false, 0f);
                    yield return LayoutSegment.FromText(emoteText, emoteTextSize, emoteElement.Style);
                    break;
                case BbCodeImageElement imageElement when options.AllowImages:
                    var texture = _textureService.GetImage(imageElement.Source);
                    var imageSize = GetScaledImageSize(texture, options.MaxImageWidth, options.MaxImageHeight);
                    yield return LayoutSegment.FromImage(imageSize, imageElement.Style with { Underline = false }, texture);
                    break;
                case BbCodeImageElement imageElement:
                    var placeholderText = $"[img]{imageElement.Source}[/img]";
                    var placeholderSize = ImGui.CalcTextSize(placeholderText, hideTextAfterDoubleHash: false, 0f);
                    yield return LayoutSegment.FromText(placeholderText, placeholderSize, imageElement.Style);
                    break;
                case BbCodeElement:
                    break;
            }
        }
    }

    private static List<LayoutLine> BuildLines(IReadOnlyList<LayoutSegment> segments, float width)
    {
        List<LayoutLine> lines = [];
        List<LayoutSegment> currentLine = [];
        float currentWidth = 0f;
        var alignment = BbCodeAlignment.Left;

        foreach (var segment in segments)
        {
            if (segment.IsNewLine)
            {
                lines.Add(new LayoutLine(currentLine, currentWidth, CalculateLineHeight(currentLine), alignment));
                currentLine = [];
                currentWidth = 0f;
                alignment = BbCodeAlignment.Left;
                continue;
            }

            if (segment.Style.Alignment != BbCodeAlignment.Left)
            {
                alignment = segment.Style.Alignment;
            }

            if (width > 0 && currentLine.Count > 0 && currentWidth + segment.Size.X > width)
            {
                lines.Add(new LayoutLine(currentLine, currentWidth, CalculateLineHeight(currentLine), alignment));
                currentLine = [];
                currentWidth = 0f;
                alignment = segment.Style.Alignment != BbCodeAlignment.Left ? segment.Style.Alignment : BbCodeAlignment.Left;
            }

            currentLine.Add(segment);
            currentWidth += segment.Size.X;
        }

        lines.Add(new LayoutLine(currentLine, currentWidth, CalculateLineHeight(currentLine), alignment));
        return lines;
    }

    private static float CalculateLineHeight(IReadOnlyList<LayoutSegment> segments)
    {
        if (segments.Count == 0)
        {
            return ImGui.GetTextLineHeight();
        }

        var maxHeight = segments.Max(s => s.Size.Y);
        return Math.Max(maxHeight, ImGui.GetTextLineHeight());
    }

    private static float CalculateAlignmentOffset(LayoutLine line, float availableWidth)
    {
        if (availableWidth <= 0 || line.Alignment == BbCodeAlignment.Left) return 0f;

        var remaining = Math.Max(0f, availableWidth - line.Width);
        return line.Alignment switch
        {
            BbCodeAlignment.Center => remaining / 2f,
            BbCodeAlignment.Right => remaining,
            _ => 0f,
        };
    }
    
    private static IEnumerable<string> SplitText(string text)
    {
        var normalized = text.Replace("\r", string.Empty);
        foreach (var segment in NormalizedTextRegex().Split(normalized))
        {
            if (segment.Length == 0) continue;
            yield return segment;
        }
    }
    
    private static Vector2 CalculateTextSize(string text, BbCodeStyle style, BbCodeRenderOptions options)
    {
        using var context = PushStyleContext(style, options);
        return ImGui.CalcTextSize(text, hideTextAfterDoubleHash: false, 0f);
    }
    private static Vector2 GetScaledImageSize(IDalamudTextureWrap? texture, float maxWidth, float maxHeight)
    {
        if (texture == null)
        {
            return new Vector2(Math.Max(16f, maxWidth / 2), Math.Max(16f, maxHeight / 2));
        }

        var width = (float)texture.Width;
        var height = (float)texture.Height;
        if (width <= maxWidth && height <= maxHeight)
        {
            return new Vector2(width, height);
        }

        var scale = Math.Min(maxWidth / width, maxHeight / height);
        return new Vector2(width * scale, height * scale);
    }

    private static void RenderSegment(LayoutSegment segment, BbCodeRenderOptions options)
    {
        if (segment.IsImage)
        {
            if (segment.Texture != null)
            {
                ImGui.Image(segment.Texture.Handle, segment.Size);
            }
            else
            {
                using var underline = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                ImGui.TextUnformatted("[image]");
            }

            return;
        }

        var color = segment.Style.Color;
        var text = segment.Text ?? string.Empty;

        using var styleContext = PushStyleContext(segment.Style, options);
        using var colorPush = color.HasValue ? ImRaii.PushColor(ImGuiCol.Text, color.Value) : null;
        
        var baseColorVector = color ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var baseColor = ImGui.GetColorU32(baseColorVector);
        using var transparentText = segment.Style.Italic ? ImRaii.PushColor(ImGuiCol.Text, Vector4.Zero) : null;
        
        ImGui.TextUnformatted(text);
        
        var rectMin = ImGui.GetItemRectMin();

        var drawList = ImGui.GetWindowDrawList();

        if (segment.Style.Italic)
        {
            RenderItalicText(drawList, rectMin, baseColor, text);
        }

        if (segment.Style.Bold)
        {
            var boldOffset = new Vector2(1f * ImGuiHelpers.GlobalScale, 0f);
            if (segment.Style.Italic)
            {
                RenderItalicText(drawList, rectMin + boldOffset, baseColor, text);
            }
            else
            {
                drawList.AddText(rectMin + boldOffset, baseColor, text);
            }
        }

        if (options.AllowLinks && (segment.Style.Url != null || segment.Style.UseTextAsUrl) && ImGui.IsItemHovered())
        {
            var url = segment.Style.Url ?? segment.Text;
            var targetUrl = url ?? string.Empty;
            if (IsAllowedLinkUrl(targetUrl))
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    if (options.OnLinkClicked != null)
                    {
                        options.OnLinkClicked(targetUrl);
                    }
                    else
                    {
                        Util.OpenLink(targetUrl);
                    }
                }
            }
        }
    }

    private static bool IsAllowedLinkUrl(string url)
    {
        return !string.IsNullOrWhiteSpace(url)
               && Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && AllowedLinkSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);
    }

    private IDalamudTextureWrap? GetTextureForEmote(string emoteName)
    {
        return _emoteMappings.TryGetValue(emoteName, out var path) ? _textureService.GetFile(path) : null;
    }

    private readonly record struct LayoutSegment(bool IsNewLine, bool IsImage, Vector2 Size, BbCodeStyle Style, string? Text, IDalamudTextureWrap? Texture)
    {
        public static LayoutSegment NewLine => new(true, false, Vector2.Zero, new BbCodeStyle(), null, null);

        public static LayoutSegment FromText(string text, Vector2 size, BbCodeStyle style)
        {
            return new(false, false, size, style, text, null);
        }

        public static LayoutSegment FromImage(Vector2 size, BbCodeStyle style, IDalamudTextureWrap? texture)
        {
            return new(false, true, size, style, null, texture);
        }
    }

    private sealed record LayoutLine(IReadOnlyList<LayoutSegment> Segments, float Width, float Height, BbCodeAlignment Alignment);
    
    private static IDisposable? PushStyleContext(BbCodeStyle style, BbCodeRenderOptions options)
    {
        _ = options;
        var targetPx = GetTargetPx(style, ImGui.GetFont().FontSize);
        return targetPx is null ? null : ElezenFonts.Push(targetPx.Value);
    }

    private static float? GetTargetPx(BbCodeStyle style, float baseSize)
    {
        if (baseSize <= 0f) return null;

        if (style.FontScale.HasValue)
        {
            var scaled = MathF.Round(baseSize * style.FontScale.Value);
            return Math.Clamp(scaled, baseSize * 0.5f, baseSize * 4f);
        }

        if (style.FontSize.HasValue)
        {
            var requestedPx = MathF.Max(1f, MathF.Round(style.FontSize.Value));
            return Math.Clamp(requestedPx, baseSize * 0.5f, baseSize * 4f);
        }

        return null;
    }


    private static void RenderItalicText(ImDrawListPtr drawList, Vector2 position, uint color, string text)
    {
        var startVertIndex = drawList.VtxBuffer.Size;
        drawList.AddText(position, color, text);
        var endVertIndex = drawList.VtxBuffer.Size;

        const float shearFactor = 0.2f;
        for (var i = startVertIndex; i < endVertIndex; i++)
        {
            var vertex = drawList.VtxBuffer[i];
            var yOffset = vertex.Pos.Y - position.Y;
            vertex.Pos.X += yOffset * shearFactor;
            drawList.VtxBuffer[i] = vertex;
        }
    }
    
    private void LoadEmoteMappings()
    {
        _emoteMappings.Clear();

        var assetDir = Path.Combine(_pluginInterface.AssemblyLocation.Directory?.FullName ?? _pluginInterface.ConfigDirectory.FullName, "Assets", "Emotes");
        foreach (var (name, path) in DiscoverEmoteFiles(assetDir))
        {
            _emoteMappings[name] = path;
        }

        foreach (var (alias, target) in _emoteAliases)
        {
            if (_emoteMappings.TryGetValue(target, out var targetPath))
            {
                _emoteMappings[alias] = targetPath;
            }
        }
    }

    private static IEnumerable<(string Name, string Path)> DiscoverEmoteFiles(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) yield break;

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var extension = Path.GetExtension(file);
            if (!EmoteExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

            var name = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(name)) continue;

            yield return (name, file);
        }
    }
    
    [GeneratedRegex(@"(\s+|\S+|\n)", RegexOptions.Compiled, matchTimeoutMilliseconds:2500)]
    private static partial Regex NormalizedTextRegex();
}