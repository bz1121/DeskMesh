using System.Text;

namespace LanSwitch.Windows.Startup;

public static class WindowsCommandLine
{
    public static string Build(string executablePath, IEnumerable<string>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var parts = new List<string> { QuoteArgument(executablePath) };
        if (arguments is not null)
        {
            parts.AddRange(arguments.Select(QuoteArgument));
        }

        return string.Join(' ', parts);
    }

    public static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length != 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', (backslashCount * 2) + 1);
                result.Append('"');
                backslashCount = 0;
                continue;
            }

            result.Append('\\', backslashCount);
            backslashCount = 0;
            result.Append(character);
        }

        result.Append('\\', backslashCount * 2);
        result.Append('"');
        return result.ToString();
    }
}
