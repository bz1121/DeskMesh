namespace LanSwitch.Windows.Startup;

public sealed record AutoStartStatus(bool IsEnabled, string? Command, bool MatchesExpectedCommand);

public interface IAutoStartManager
{
    AutoStartStatus GetStatus(string? expectedExecutablePath = null, IReadOnlyList<string>? arguments = null);

    void Enable(string executablePath, IReadOnlyList<string>? arguments = null);

    bool Disable();
}
