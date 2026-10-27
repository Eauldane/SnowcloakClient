namespace Snowcloak.Core.Chat;

public static class ChatSpeedDialPolicy
{
    public const int Maximum = 999;

    public static bool TryAssign(IDictionary<int, string> assignments, int number, ConversationKey key)
    {
        if (number is < 1 or > Maximum || !Enum.IsDefined(key.Kind) || string.IsNullOrWhiteSpace(key.Id)) return false;
        var target = key.ToString();
        if (assignments.TryGetValue(number, out var existing) && !string.Equals(existing, target, StringComparison.Ordinal)) return false;
        foreach (var previous in assignments.Where(item => string.Equals(item.Value, target, StringComparison.Ordinal)).Select(item => item.Key).ToArray())
            assignments.Remove(previous);
        assignments[number] = target;
        return true;
    }

    public static ConversationKey? Resolve(IReadOnlyDictionary<int, string> assignments, int number)
        => number is >= 1 and <= Maximum && assignments.TryGetValue(number, out var value)
            && ConversationKey.TryParse(value, out var key) ? key : null;
}
