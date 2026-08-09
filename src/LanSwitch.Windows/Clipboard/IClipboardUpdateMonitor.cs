namespace LanSwitch.Windows.Clipboard;

public interface IClipboardStaDispatcher
{
    Task InvokeAsync(Action operation, CancellationToken cancellationToken = default);

    Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken = default);
}

public interface IClipboardUpdateMonitor : IClipboardStaDispatcher, IDisposable
{
    event EventHandler<ClipboardUpdatedEventArgs>? ClipboardUpdated;

    bool IsRunning { get; }

    void Start();

    void Stop();
}

public sealed class ClipboardUpdatedEventArgs(long sequence, DateTimeOffset timestampUtc) : EventArgs
{
    public long Sequence { get; } = sequence;

    public DateTimeOffset TimestampUtc { get; } = timestampUtc;
}

public static class ClipboardWindowMessages
{
    public const uint ClipboardUpdate = 0x031D;

    public static bool IsClipboardUpdate(uint message) => message == ClipboardUpdate;
}
