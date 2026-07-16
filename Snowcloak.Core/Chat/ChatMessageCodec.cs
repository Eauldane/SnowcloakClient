namespace Snowcloak.Core.Chat;

using System.Text;
using System.Text.RegularExpressions;

public static partial class ChatMessageCodec
{
    public static byte[] Encode(string input) => Encoding.UTF8.GetBytes(input);

    public static string DecodeText(byte[] payload) => Encoding.UTF8.GetString(payload);

    public static IReadOnlyList<ChatSegment> Decode(byte[] payload) => Decode(DecodeText(payload));

    public static IReadOnlyList<ChatSegment> Decode(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return [];
        }

        List<ChatSegment> segments = [];
        var cursor = 0;
        foreach (Match match in TokenRegex().Matches(input))
        {
            if (match.Index > cursor)
            {
                AddMarkup(segments, input[cursor..match.Index]);
            }

            if (match.Groups["mention"].Success)
            {
                segments.Add(new MentionSegment(match.Groups["uid"].Value));
            }
            else if (match.Groups["url"].Success)
            {
                var url = match.Groups["url"].Value;
                segments.Add(new LinkSegment(url, url));
            }
            else
            {
                segments.Add(new EmoteSegment(match.Groups["emote"].Value));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < input.Length)
        {
            AddMarkup(segments, input[cursor..]);
        }

        return segments;
    }

    public static string Flatten(IEnumerable<ChatSegment> segments, Func<string, string>? mentionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case TextSegment text:
                    builder.Append(text.Text);
                    break;
                case BbSegment bb:
                    builder.Append(BbTagRegex().Replace(bb.Markup, string.Empty));
                    break;
                case LinkSegment link:
                    builder.Append(link.Text);
                    break;
                case MentionSegment mention:
                    builder.Append('@').Append(mentionResolver?.Invoke(mention.Uid) ?? mention.Uid);
                    break;
                case EmoteSegment emote:
                    builder.Append(':').Append(emote.Name).Append(':');
                    break;
            }
        }

        return builder.ToString();
    }

    private static void AddMarkup(List<ChatSegment> segments, string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            segments.Add(value.Contains('[', StringComparison.Ordinal)
                ? new BbSegment(value)
                : new TextSegment(value));
        }
    }

    [GeneratedRegex(@"(?<mention>\[mention:(?<uid>[A-Za-z0-9]{1,32})\])|(?<url>https?://[^\s\[\]<>]+)|:(?<emote>[A-Za-z0-9_]+):", RegexOptions.Compiled | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\[/?(?:b|i|u|color|colour|size|center|right|align|url|list|ul|ol)(?:=[^\]]+)?\]", RegexOptions.Compiled | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex BbTagRegex();
}
