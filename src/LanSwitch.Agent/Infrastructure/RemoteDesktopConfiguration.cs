namespace LanSwitch.Agent.Infrastructure;

public static class RemoteDesktopConfiguration
{
    public const int MinimumFramesPerSecond = 2;
    public const int MaximumFramesPerSecond = 90;
    public const int MinimumJpegQuality = 30;
    public const int MaximumJpegQuality = 85;
    public const int DefaultFramesPerSecond = 30;
    public const int DefaultJpegQuality = 60;

    public static AgentSettings Normalize(AgentSettings settings) => settings with
    {
        RemoteDesktopFramesPerSecond = Math.Clamp(
            settings.RemoteDesktopFramesPerSecond,
            MinimumFramesPerSecond,
            MaximumFramesPerSecond),
        RemoteDesktopJpegQuality = Math.Clamp(
            settings.RemoteDesktopJpegQuality,
            MinimumJpegQuality,
            MaximumJpegQuality)
    };

    public static void Validate(int framesPerSecond, int jpegQuality)
    {
        if (framesPerSecond is < MinimumFramesPerSecond or > MaximumFramesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "远程桌面帧率必须在 2 到 90 之间。");
        if (jpegQuality is < MinimumJpegQuality or > MaximumJpegQuality)
            throw new ArgumentOutOfRangeException(nameof(jpegQuality), "远程桌面画质必须在 30 到 85 之间。");
    }

    public static RemoteDesktopSettingsView GetView(AgentSettings settings) => new(
        settings.RemoteDesktopEnabled,
        settings.RemoteDesktopFramesPerSecond,
        settings.RemoteDesktopJpegQuality);
}

public sealed record RemoteDesktopSettingsView(bool Enabled, int FramesPerSecond, int JpegQuality);

public sealed record RemoteDesktopSettingsUpdateRequest(bool Enabled, int FramesPerSecond, int JpegQuality);
