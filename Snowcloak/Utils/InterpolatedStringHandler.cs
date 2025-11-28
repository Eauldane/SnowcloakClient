using System.Runtime.CompilerServices;
using System.Text;

namespace Snowcloak.Utils;

[InterpolatedStringHandler]
public readonly ref struct InterpolatedStringHandler
{
    readonly StringBuilder _logMessageStringbuilder;

    public InterpolatedStringHandler(int literalLength, int formattedCount)
    {
        _logMessageStringbuilder = new StringBuilder(literalLength);
    }

    public void AppendLiteral(string s)
    {
        _logMessageStringbuilder.Append(s);
    }

    public void AppendFormatted<T>(T t)
    {
        _logMessageStringbuilder.Append(t?.ToString());
    }

    public string BuildMessage() => _logMessageStringbuilder.ToString();
}
