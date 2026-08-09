using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using LanSwitch.Windows.Clipboard;

namespace LanSwitch.Agent.Services;

public sealed class ClipboardWorker(ClipboardCoordinator coordinator, ILogger<ClipboardWorker> logger) : BackgroundService
{
    private const int MaxConcurrentRemoteApplications = 4;
    private readonly IClipboardUpdateMonitor _monitor = new WindowsClipboardUpdateMonitor();
    private readonly SemaphoreSlim _remoteApplySlots = new(
        MaxConcurrentRemoteApplications,
        MaxConcurrentRemoteApplications);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _monitor.ClipboardUpdated += (_, _) => _ = OnClipboardChangedAsync();
        _monitor.Start();
        stoppingToken.Register(() =>
        {
            try { _monitor.Stop(); }
            catch (Exception exception) { logger.LogWarning(exception, "停止剪贴板 STA 线程超时"); }
        });
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public async Task ApplyRemoteAsync(ClipboardPacket packet, CancellationToken cancellationToken)
    {
        if (!_monitor.IsRunning) throw new InvalidOperationException("剪贴板线程尚未启动。");
        await _monitor.InvokeAsync(() =>
        {
            var data = Convert.FromBase64String(packet.DataBase64);
            if (packet.Kind == "text") Clipboard.SetText(Encoding.UTF8.GetString(data), TextDataFormat.UnicodeText);
            else if (packet.Kind == "image")
            {
                using var stream = new MemoryStream(data, writable: false);
                using var source = Image.FromStream(stream);
                var pixels = checked((long)source.Width * source.Height);
                if (source.Width is <= 0 or > 32768 || source.Height is <= 0 or > 32768 ||
                    pixels > 64L * 1024 * 1024 || pixels * 4 > 256L * 1024 * 1024)
                    throw new InvalidDataException("剪贴板图片尺寸过大，已拒绝解码。");
                using var copy = new Bitmap(source);
                Clipboard.SetImage(copy);
            }
        }, cancellationToken);
    }

    public IDisposable? TryAcquireRemoteApplySlot() =>
        _remoteApplySlots.Wait(0) ? new RemoteApplyLease(_remoteApplySlots) : null;

    public Task SetLocalTextAsync(string value, CancellationToken cancellationToken)
    {
        if (!_monitor.IsRunning) throw new InvalidOperationException("剪贴板线程尚未启动。");
        return _monitor.InvokeAsync(
            () => Clipboard.SetText(value, TextDataFormat.UnicodeText),
            cancellationToken);
    }

    private async Task OnClipboardChangedAsync()
    {
        try
        {
            if (!_monitor.IsRunning) return;
            var snapshot = await _monitor.InvokeAsync(() =>
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    return new ClipboardCapture("text", Encoding.UTF8.GetBytes(text), ClipboardCoordinator.SafePreview(text));
                }
                if (Clipboard.ContainsImage())
                {
                    using var image = Clipboard.GetImage();
                    if (image is null) return null;
                    using var stream = new MemoryStream();
                    image.Save(stream, ImageFormat.Png);
                    return new ClipboardCapture("image", stream.ToArray(), null);
                }
                return null;
            }, CancellationToken.None);
            if (snapshot is not null)
                await coordinator.PublishLocalAsync(snapshot.Kind, snapshot.Data, snapshot.Preview, CancellationToken.None);
        }
        catch (ExternalException exception)
        {
            logger.LogDebug(exception, "剪贴板暂时被其他程序占用");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "读取剪贴板失败");
        }
    }

    private sealed record ClipboardCapture(string Kind, byte[] Data, string? Preview);

    private sealed class RemoteApplyLease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
