namespace Snowcloak.UI.Components.BbCode;

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

internal static partial class BbCodeParser
{
    private static readonly BbCodeStyle DefaultStyle = new();
    private enum ListKind
    {
        Unordered,
        Decimal,
        LowerAlpha,
        UpperAlpha,
    }

    private record struct ListContext(ListKind Kind, int Counter);
    
    public static IReadOnlyList<BbCodeElement> Parse(string input, ISet<string> knownEmotes)
    {
        List<BbCodeElement> elements = [];
        var sanitizedInput = input.Replace("\r", string.Empty);
        var styleStack = new Stack<BbCodeStyle>();
        styleStack.Push(DefaultStyle);

        var listStack = new Stack<ListContext>();
        
        var span = sanitizedInput.AsSpan();
        var currentIndex = 0;
        while (currentIndex < span.Length)
        {
            var match = TagRegex().Match(sanitizedInput, currentIndex);
            if (!match.Success)
            {
                AddText(span[currentIndex..].ToString(), styleStack.Peek(), knownEmotes, elements);
                break;
            }

            if (match.Index > currentIndex)
            {
                AddText(span[currentIndex..match.Index].ToString(), styleStack.Peek(), knownEmotes, elements);
            }

            var tagName = match.Groups[2].Value.ToLowerInvariant();
            var tagArgument = match.Groups[3].Value;
            var isClosingTag = !string.IsNullOrEmpty(match.Groups[1].Value);
            currentIndex = match.Index + match.Length;

            if (isClosingTag)
            {
                if (IsListTag(tagName) && listStack.Count > 0)
                {
                    listStack.Pop();
                }
                
                if (styleStack.Count > 1)
                {
                    styleStack.Pop();
                }

                continue;
            }

            switch (tagName)
            {
                case "b":
                    styleStack.Push(styleStack.Peek() with { Bold = true });
                    break;
                case "i":
                    styleStack.Push(styleStack.Peek() with { Italic = true });
                    break;
                case "u":
                    styleStack.Push(styleStack.Peek() with { Underline = true });
                    break;
                case "color":
                case "colour":
                    if (TryParseColor(tagArgument, out var color))
                    {
                        styleStack.Push(styleStack.Peek() with { Color = color });
                    }
                    else
                    {
                        styleStack.Push(styleStack.Peek());
                    }
                    break;
                case "size":
                    if (TryParseSize(tagArgument, out var sizePx, out var scale))
                    {
                        styleStack.Push(styleStack.Peek() with { FontSize = sizePx, FontScale = scale });
                    }
                    else
                    {
                        styleStack.Push(styleStack.Peek());
                    }

                    break;
                case "center":
                    styleStack.Push(styleStack.Peek() with { Alignment = BbCodeAlignment.Center });
                    break;
                case "right":
                    styleStack.Push(styleStack.Peek() with { Alignment = BbCodeAlignment.Right });
                    break;
                case "align":
                    styleStack.Push(styleStack.Peek() with { Alignment = ParseAlignment(tagArgument) });
                    break;
                case "url":
                    var url = string.IsNullOrWhiteSpace(tagArgument) ? null : tagArgument.Trim();
                    styleStack.Push(styleStack.Peek() with { Url = url, UseTextAsUrl = string.IsNullOrWhiteSpace(url), Underline = true });
                    break;
                case "img":
                    var closingIndex = IndexOfClosingTag(sanitizedInput, currentIndex, "img");
                    if (closingIndex >= 0)
                    {
                        var source = sanitizedInput[currentIndex..closingIndex];
                        elements.Add(new BbCodeImageElement(source.Trim(), styleStack.Peek()));
                        currentIndex = closingIndex + "[/img]".Length;
                    }
                    else
                    {
                        AddText(match.Value, styleStack.Peek(), knownEmotes, elements);
                    }
                    break;
                case "list":
                    listStack.Push(ParseListContext(tagArgument));
                    styleStack.Push(styleStack.Peek());
                    break;
                case "ul":
                    listStack.Push(new ListContext(ListKind.Unordered, 1));
                    styleStack.Push(styleStack.Peek());
                    break;
                case "ol":
                    listStack.Push(ParseListContext(tagArgument, ListKind.Decimal));
                    styleStack.Push(styleStack.Peek());
                    break;
                case "*":
                    if (listStack.Count == 0)
                    {
                        AddText(match.Value, styleStack.Peek(), knownEmotes, elements);
                        break;
                    }

                    AddListItem(elements, styleStack.Peek(), listStack);
                    break;
                default:
                    AddText(match.Value, styleStack.Peek(), knownEmotes, elements);
                    break;
            }
        }

        return elements;
    }

    private static void AddText(string text, BbCodeStyle style, ISet<string> knownEmotes, List<BbCodeElement> elements)
    {
        if (string.IsNullOrEmpty(text)) return;

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                elements.Add(new BbCodeNewLineElement());
            }

            var line = lines[i];
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var matches = EmoteRegex().Matches(line);
            var cursor = 0;
            foreach (Match match in matches)
            {
                if (match.Index > cursor)
                {
                    elements.Add(new BbCodeTextElement(line[cursor..match.Index], style));
                }

                var emoteName = match.Groups[1].Value;
                if (knownEmotes.Contains(emoteName))
                {
                    elements.Add(new BbCodeEmoteElement(emoteName, style));
                }
                else
                {
                    elements.Add(new BbCodeTextElement(match.Value, style));
                }

                cursor = match.Index + match.Length;
            }

            if (cursor < line.Length)
            {
                elements.Add(new BbCodeTextElement(line[cursor..], style));
            }
        }
    }

    private static int IndexOfClosingTag(string input, int startIndex, string tagName)
    {
        return input.IndexOf($"[/{tagName}]", startIndex, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddListItem(List<BbCodeElement> elements, BbCodeStyle style, Stack<ListContext> listStack)
    {
        if (listStack.Count == 0) return;

        var context = listStack.Pop();
        var marker = GetListMarker(context);
        context = context with { Counter = context.Counter + 1 };
        listStack.Push(context);

        if (elements.Count > 0 && elements[^1] is not BbCodeNewLineElement)
        {
            elements.Add(new BbCodeNewLineElement());
        }

        var indentLevel = Math.Max(0, listStack.Count - 1);
        var indent = new string(' ', indentLevel * 2);
        elements.Add(new BbCodeTextElement($"{indent}{marker} ", style));
    }

    private static bool TryParseColor(string? input, out Vector4 color)
    {
        color = Vector4.Zero;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim().TrimStart('#');
        if (trimmed.Length == 6 && int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            var r = (byte)((hex & 0xFF0000) >> 16) / 255f;
            var g = (byte)((hex & 0x00FF00) >> 8) / 255f;
            var b = (byte)(hex & 0x0000FF) / 255f;
            color = new Vector4(r, g, b, 1f);
            return true;
        }

        return false;
    }
    private static bool TryParseSize(string? input, out float? sizePx, out float? scale)
    {
        sizePx = null;
        scale = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (trimmed.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(trimmed.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percentValue))
        {
            scale = percentValue / 100f;
            return scale > 0;
        }

        if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue) && numericValue > 0)
        {
            if (numericValue <= 5f)
            {
                scale = numericValue;
            }
            else
            {
                sizePx = numericValue;
            }

            return true;
        }

        return false;
    }
    
    private static BbCodeAlignment ParseAlignment(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return BbCodeAlignment.Left;

        var trimmed = input.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "center" or "centre" => BbCodeAlignment.Center,
            "right" => BbCodeAlignment.Right,
            _ => BbCodeAlignment.Left,
        };
    }

    private static ListContext ParseListContext(string? input, ListKind defaultKind = ListKind.Unordered)
    {
        if (string.IsNullOrWhiteSpace(input)) return new ListContext(defaultKind, 1);
        
        var trimmed = input.Trim();
        if (int.TryParse(trimmed, out var numeric) && numeric > 0)
        {
            return new ListContext(ListKind.Decimal, numeric);
        }

        return trimmed.ToLowerInvariant() switch
        {
            "a" => new ListContext(ListKind.LowerAlpha, 1),
            "a+" => new ListContext(ListKind.LowerAlpha, 1),
            "A" => new ListContext(ListKind.UpperAlpha, 1),
            _ => new ListContext(defaultKind, 1),
            
        };
    }

    private static bool IsListTag(string tagName)
    {
        return tagName is "list" or "ul" or "ol";
    }
    
    private static string GetListMarker(ListContext context)
    {
        return context.Kind switch
        {
            ListKind.Unordered => "•",
            ListKind.Decimal => context.Counter.ToString(CultureInfo.InvariantCulture),
            ListKind.LowerAlpha => ((char)('a' + (context.Counter - 1) % 26)).ToString(),
            ListKind.UpperAlpha => ((char)('A' + (context.Counter - 1) % 26)).ToString(),
            _ => "•",
        };
    }

    [GeneratedRegex(@"\[(/?)(\w+|\*)(?:=([^\]]+))?\]", RegexOptions.Compiled)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@":([a-zA-Z0-9_]+):", RegexOptions.Compiled)]
    private static partial Regex EmoteRegex();
}