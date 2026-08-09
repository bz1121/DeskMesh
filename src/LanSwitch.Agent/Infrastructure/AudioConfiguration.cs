namespace LanSwitch.Agent.Infrastructure;

public static class AudioConfiguration
{
    public const int MinimumVolume = 0;
    public const int MaximumVolume = 100;

    public static AgentSettings Normalize(AgentSettings settings) => settings with
    {
        AudioVolume = Math.Clamp(settings.AudioVolume, MinimumVolume, MaximumVolume)
    };

    public static int ValidateVolume(int volume)
    {
        if (volume is < MinimumVolume or > MaximumVolume)
            throw new ArgumentOutOfRangeException(nameof(volume), "音频音量必须在 0 到 100 之间。");
        return volume;
    }
}

public sealed record AudioSettingsView(
    bool Enabled,
    int Volume,
    string Phase,
    string? ActiveDeviceId,
    string? ActiveDeviceName,
    string Message);

public sealed record AudioSettingsUpdateRequest(bool Enabled, int Volume);
