namespace LanSwitch.Core.Internal;

internal static class HexUtility
{
    public static bool IsSha256(string? value)
    {
        return value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    }

    public static string NormalizeSha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        if (!IsSha256(normalized))
        {
            throw new FormatException("A SHA-256 value must contain exactly 64 hexadecimal characters.");
        }

        return normalized;
    }
}
