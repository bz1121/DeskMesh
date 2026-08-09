using Microsoft.Win32;

namespace LanSwitch.Agent.Tray;

internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskMesh";
    private const string LegacyValueName = "LanSwitch";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        var expected = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(expected)) return false;
        return PointsToCurrentExecutable(key?.GetValue(ValueName), expected) ||
            PointsToCurrentExecutable(key?.GetValue(LegacyValueName), expected);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法取得程序路径。");
            key.SetValue(ValueName, $"\"{executable}\"");
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
    }

    internal static bool PointsToCurrentExecutable(object? registryValue, string expectedPath)
    {
        if (registryValue is not string value || string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var candidate = value.Trim();
            if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
                candidate = candidate[1..^1];
            else if (candidate.Contains('"'))
                return false;
            return string.Equals(
                Path.GetFullPath(candidate),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
