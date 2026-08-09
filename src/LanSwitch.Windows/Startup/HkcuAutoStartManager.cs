using Microsoft.Win32;

namespace LanSwitch.Windows.Startup;

public sealed class HkcuAutoStartManager : IAutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _applicationName;

    public HkcuAutoStartManager(string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        if (applicationName.IndexOfAny(['\\', '/', '\0']) >= 0)
        {
            throw new ArgumentException("The startup value name contains an invalid character.", nameof(applicationName));
        }

        _applicationName = applicationName;
    }

    public AutoStartStatus GetStatus(
        string? expectedExecutablePath = null,
        IReadOnlyList<string>? arguments = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = key?.GetValue(_applicationName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string;
        if (command is null)
        {
            return new AutoStartStatus(false, null, expectedExecutablePath is null);
        }

        if (expectedExecutablePath is null)
        {
            return new AutoStartStatus(true, command, true);
        }

        var expectedCommand = WindowsCommandLine.Build(Path.GetFullPath(expectedExecutablePath), arguments);
        return new AutoStartStatus(
            true,
            command,
            string.Equals(command, expectedCommand, StringComparison.Ordinal));
    }

    public void Enable(string executablePath, IReadOnlyList<string>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The startup executable path must be absolute.", nameof(executablePath));
        }

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The startup executable does not exist.", fullPath);
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(
            _applicationName,
            WindowsCommandLine.Build(fullPath, arguments),
            RegistryValueKind.String);
    }

    public bool Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(_applicationName) is null)
        {
            return false;
        }

        key.DeleteValue(_applicationName, throwOnMissingValue: false);
        return true;
    }
}
