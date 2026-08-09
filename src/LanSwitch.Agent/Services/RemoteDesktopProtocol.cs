using System.Drawing;
using System.Text.Json;

namespace LanSwitch.Agent.Services;

internal static class RemoteDesktopProtocol
{
    internal const int Version = 1;
    internal const string SubProtocol = "lanswitch.remote-desktop.v1";
    internal const int MaximumControlMessageBytes = 16 * 1024;
    internal const int MaximumFrameBytes = 8 * 1024 * 1024;
    internal const int MaximumFrameWidth = 1920;
    internal const int MaximumFrameHeight = 1080;
    internal const long MaximumSourcePixels = 40_000_000;
    internal const int MaximumInputMessagesPerSecond = 500;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool IsValidSessionId(string? value) =>
        value is { Length: 32 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static Size ScaleToFit(Size source)
    {
        if (source.Width <= 0 || source.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(source), "远程屏幕尺寸无效。");
        if (checked((long)source.Width * source.Height) > MaximumSourcePixels)
            throw new InvalidOperationException("远程屏幕像素数量超过安全限制。");
        var scale = Math.Min(1d, Math.Min(
            MaximumFrameWidth / (double)source.Width,
            MaximumFrameHeight / (double)source.Height));
        return new Size(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)));
    }
}

internal sealed class RemoteDesktopSessionRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    internal IDisposable Activate(string peerId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(peerId)) throw new ArgumentException("远程设备 ID 不能为空。", nameof(peerId));
        if (!RemoteDesktopProtocol.IsValidSessionId(sessionId))
            throw new ArgumentException("远程桌面会话 ID 无效。", nameof(sessionId));
        var key = Key(peerId, sessionId);
        lock (_gate)
        {
            if (!_active.Add(key)) throw new InvalidOperationException("远程桌面会话已经存在。");
        }
        return new Registration(this, key);
    }

    internal bool IsActive(string peerId, string sessionId)
    {
        if (!RemoteDesktopProtocol.IsValidSessionId(sessionId)) return false;
        lock (_gate) return _active.Contains(Key(peerId, sessionId));
    }

    private void Deactivate(string key)
    {
        lock (_gate) _active.Remove(key);
    }

    private static string Key(string peerId, string sessionId) => $"{peerId}\0{sessionId}";

    private sealed class Registration(RemoteDesktopSessionRegistry owner, string key) : IDisposable
    {
        private RemoteDesktopSessionRegistry? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Deactivate(key);
    }
}

internal static class RemoteDesktopPointerMapper
{
    internal static Point MapToNormalizedVirtualDesktop(
        double x,
        double y,
        Rectangle displayBounds,
        Rectangle virtualDesktopBounds)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(x), "鼠标坐标必须在 0 到 1 之间。");
        if (displayBounds.Width <= 0 || displayBounds.Height <= 0 ||
            virtualDesktopBounds.Width <= 0 || virtualDesktopBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayBounds), "远程桌面边界无效。");

        var pixelX = displayBounds.Left + (int)Math.Round(x * Math.Max(0, displayBounds.Width - 1));
        var pixelY = displayBounds.Top + (int)Math.Round(y * Math.Max(0, displayBounds.Height - 1));
        var normalizedX = Normalize(pixelX - virtualDesktopBounds.Left, virtualDesktopBounds.Width);
        var normalizedY = Normalize(pixelY - virtualDesktopBounds.Top, virtualDesktopBounds.Height);
        return new Point(normalizedX, normalizedY);
    }

    private static int Normalize(int offset, int length)
    {
        if (length <= 1) return 0;
        return Math.Clamp((int)Math.Round(offset * 65535d / (length - 1)), 0, 65535);
    }
}

public sealed record RemoteDesktopDisplay(
    int Index,
    string DeviceName,
    int Width,
    int Height,
    int Left,
    int Top,
    bool Primary);

internal sealed record RemoteDesktopStreamHeader(
    string Type,
    int ProtocolVersion,
    RemoteDesktopDisplay Display,
    int FrameWidth,
    int FrameHeight,
    int FramesPerSecond,
    int JpegQuality);

internal sealed record RemoteDesktopInputMessage(
    string Type,
    double? X = null,
    double? Y = null,
    int? Button = null,
    int? Delta = null,
    int? VirtualKey = null,
    bool? Down = null,
    bool? Extended = null);
