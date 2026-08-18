using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Core.Privileged;
using LanSwitch.Windows.Input;

namespace LanSwitch.Agent.Services;

public sealed class RemoteDesktopService : IDisposable
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders()
        .Single(static codec => codec.FormatID == ImageFormat.Jpeg.Guid);
    private readonly DeviceIdentity _identity;
    private readonly SettingsStore _settings;
    private readonly AppState _state;
    private readonly PeerDirectory _peers;
    private readonly PeerHttpClientFactory _clients;
    private readonly AudioRelayService _audio;
    private readonly RemoteDesktopOutgoingSessionRegistry _outgoingSessions;
    private readonly PrivilegedBridgeClient _privilegedBridge;
    private readonly ILogger<RemoteDesktopService> _logger;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly RemoteDesktopSessionRegistry _sessions = new();
    private readonly IWindowsInputInjector _injector = new WindowsInputInjector();
    private int _disposed;

    public RemoteDesktopService(
        DeviceIdentity identity,
        SettingsStore settings,
        AppState state,
        PeerDirectory peers,
        PeerHttpClientFactory clients,
        AudioRelayService audio,
        RemoteDesktopOutgoingSessionRegistry outgoingSessions,
        PrivilegedBridgeClient privilegedBridge,
        ILogger<RemoteDesktopService> logger)
    {
        _identity = identity;
        _settings = settings;
        _state = state;
        _peers = peers;
        _clients = clients;
        _audio = audio;
        _outgoingSessions = outgoingSessions;
        _privilegedBridge = privilegedBridge;
        _logger = logger;
        _injector.Start();
    }

    public IReadOnlyList<RemoteDesktopDisplay> GetLocalDisplays() => GetScreens()
        .Select(static item => item.Display)
        .ToArray();

    public async Task<IReadOnlyList<RemoteDesktopDisplay>> GetRemoteDisplaysAsync(
        string targetDeviceId,
        CancellationToken cancellationToken)
    {
        var target = RequireTarget(targetDeviceId);
        if (!_peers.TryGetTrustToken(target.Id, target.Fingerprint, out var trustToken))
            throw new UnauthorizedAccessException("目标设备信任已被撤销。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, trustToken);
        using var client = _clients.Create(target);
        using var response = await client.GetAsync("/peer/v1/remote-desktop/displays", linked.Token);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<RemoteDesktopError>(
                RemoteDesktopProtocol.JsonOptions,
                linked.Token);
            throw new InvalidOperationException(error?.Error ?? $"远端拒绝读取屏幕（HTTP {(int)response.StatusCode}）。");
        }
        return await response.Content.ReadFromJsonAsync<RemoteDesktopDisplay[]>(
                   RemoteDesktopProtocol.JsonOptions,
                   linked.Token)
               ?? [];
    }

    public async Task<RemoteDesktopDisplayCatalog> GetRemoteDisplayCatalogAsync(
        string targetDeviceId,
        CancellationToken cancellationToken)
    {
        var generation = _outgoingSessions.CaptureGeneration();
        var displays = await GetRemoteDisplaysAsync(targetDeviceId, cancellationToken);
        if (!_outgoingSessions.IsGenerationCurrent(generation))
            throw new InvalidOperationException(
                "读取屏幕期间发生了回本机操作，请重新读取屏幕后再连接。");
        return new RemoteDesktopDisplayCatalog(generation, displays);
    }

    public async Task ProxyAsync(
        WebSocket browser,
        string targetDeviceId,
        int displayIndex,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        var target = RequireTarget(targetDeviceId);
        using var outgoingSession = _outgoingSessions.BeginSession(target.Id, expectedGeneration);
        if (!_peers.TryGetTrustToken(target.Id, target.Fingerprint, out var trustToken))
            throw new UnauthorizedAccessException("目标设备信任已被撤销。");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            trustToken,
            outgoingSession.CancellationToken);
        var desktopSessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var remote = DefaultInputWebSocketFactory.CreateClientSocket(_identity, target);
        remote.Options.AddSubProtocol(RemoteDesktopProtocol.SubProtocol);
        var address = PeerHttpClientFactory.BuildBaseAddress(target.Address, target.Port);
        var uri = new UriBuilder(address)
        {
            Scheme = "wss",
            Path = "/peer/v1/remote-desktop/stream",
            Query = $"displayIndex={displayIndex}&desktopSessionId={desktopSessionId}"
        }.Uri;

        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        connectDeadline.CancelAfter(TimeSpan.FromSeconds(3));
        await remote.ConnectAsync(uri, connectDeadline.Token);
        _state.AddDiagnostic("success", "远程桌面", $"已连接 {target.Name} 的远程画面。");

        var audio = RunRemoteDesktopAudioAsync(target, desktopSessionId, linked.Token);

        var frames = PumpWebSocketAsync(
            remote,
            browser,
            RemoteDesktopProtocol.MaximumFrameBytes,
            linked.Token);
        var controls = PumpWebSocketAsync(
            browser,
            remote,
            RemoteDesktopProtocol.MaximumControlMessageBytes,
            linked.Token);
        await Task.WhenAny(frames, controls);
        linked.Cancel();
        var frameFailure = await ObservePumpAsync(frames);
        var controlFailure = await ObservePumpAsync(controls);
        _ = await ObservePumpAsync(audio);
        Rethrow(frameFailure ?? controlFailure);
    }

    public async Task ServeAsync(
        WebSocket socket,
        RuntimePeer peer,
        int displayIndex,
        string desktopSessionId,
        CancellationToken cancellationToken)
    {
        if (!await _captureGate.WaitAsync(0, cancellationToken))
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.PolicyViolation,
                "已有远程桌面会话正在使用本机屏幕。",
                CancellationToken.None);
            return;
        }

        try
        {
            using var registration = _sessions.Activate(peer.Id, desktopSessionId);
            var selected = GetScreens().FirstOrDefault(item => item.Display.Index == displayIndex)
                ?? throw new ArgumentOutOfRangeException(nameof(displayIndex), "指定的远程屏幕不存在。");
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var capture = CaptureLoopAsync(socket, selected.Screen, selected.Display, session.Token);
            var input = ReceiveInputLoopAsync(socket, selected.Screen.Bounds, session.Token);
            _state.AddDiagnostic("success", "远程桌面", $"{peer.Name} 开始查看并控制 {selected.Display.DeviceName}。");

            await Task.WhenAny(capture, input);
            session.Cancel();
            var captureFailure = await ObservePumpAsync(capture);
            var inputFailure = await ObservePumpAsync(input);
            Rethrow(captureFailure ?? inputFailure);
        }
        finally
        {
            var release = _injector.ReleaseAll();
            if (!release.Succeeded)
                _state.AddDiagnostic("warning", "远程桌面", $"会话结束时仍有 {release.Remaining} 个远端按键未确认释放。");
            _captureGate.Release();
        }
    }

    public bool IsIncomingSessionActive(string peerId, string desktopSessionId) =>
        _sessions.IsActive(peerId, desktopSessionId);

    private async Task RunRemoteDesktopAudioAsync(
        RuntimePeer target,
        string desktopSessionId,
        CancellationToken cancellationToken)
    {
        if (!_settings.Snapshot.AudioForwardingEnabled) return;
        Exception? failure = null;
        for (var attempt = 0; attempt < 4 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await _audio.ReceiveRemoteDesktopAudioAsync(target, desktopSessionId, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failure = exception;
                if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }

        if (failure is null) return;
        var message = $"{target.Name} 的系统音频未接通，远程画面和输入保持可用：{failure.Message}";
        _state.AddDiagnostic("warning", "远程桌面音频", message);
        _logger.LogWarning(failure, "远程桌面音频连接失败：{Peer}", target.Name);
    }

    private async Task CaptureLoopAsync(
        WebSocket socket,
        Screen screen,
        RemoteDesktopDisplay display,
        CancellationToken cancellationToken)
    {
        var config = _settings.Snapshot;
        var frameSize = RemoteDesktopProtocol.ScaleToFit(screen.Bounds.Size);
        var header = new RemoteDesktopStreamHeader(
            "hello",
            RemoteDesktopProtocol.Version,
            display,
            frameSize.Width,
            frameSize.Height,
            config.RemoteDesktopFramesPerSecond,
            config.RemoteDesktopJpegQuality);
        await socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(header, RemoteDesktopProtocol.JsonOptions),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

        using var source = new Bitmap(screen.Bounds.Width, screen.Bounds.Height, PixelFormat.Format24bppRgb);
        using var sourceGraphics = Graphics.FromImage(source);
        using var scaled = frameSize == source.Size
            ? null
            : new Bitmap(frameSize.Width, frameSize.Height, PixelFormat.Format24bppRgb);
        using var scaledGraphics = scaled is null ? null : Graphics.FromImage(scaled);
        if (scaledGraphics is not null)
        {
            scaledGraphics.CompositingMode = CompositingMode.SourceCopy;
            scaledGraphics.CompositingQuality = CompositingQuality.HighSpeed;
            scaledGraphics.InterpolationMode = InterpolationMode.Bilinear;
            scaledGraphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            scaledGraphics.SmoothingMode = SmoothingMode.None;
        }
        using var quality = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)config.RemoteDesktopJpegQuality);
        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = quality;
        using var output = new MemoryStream(512 * 1024);
        var interval = TimeSpan.FromSeconds(1d / config.RemoteDesktopFramesPerSecond);

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            if (!_settings.Snapshot.RemoteDesktopEnabled) break;
            var started = Stopwatch.GetTimestamp();
            sourceGraphics.CopyFromScreen(
                screen.Bounds.Location,
                Point.Empty,
                screen.Bounds.Size,
                CopyPixelOperation.SourceCopy);
            Image encoded = source;
            if (scaled is not null && scaledGraphics is not null)
            {
                scaledGraphics.DrawImage(source, new Rectangle(Point.Empty, frameSize));
                encoded = scaled;
            }

            output.Position = 0;
            output.SetLength(0);
            encoded.Save(output, JpegCodec, encoderParameters);
            if (output.Length > RemoteDesktopProtocol.MaximumFrameBytes)
                throw new InvalidDataException("远程桌面画面超过单帧大小限制。");
            await socket.SendAsync(
                output.GetBuffer().AsMemory(0, checked((int)output.Length)),
                WebSocketMessageType.Binary,
                true,
                cancellationToken);

            var remaining = interval - Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
        }
    }

    private async Task ReceiveInputLoopAsync(
        WebSocket socket,
        Rectangle displayBounds,
        CancellationToken cancellationToken)
    {
        var rateWindow = Stopwatch.GetTimestamp();
        var messagesInWindow = 0;
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            if (!_settings.Snapshot.RemoteDesktopEnabled) break;
            byte[]? payload;
            using (var lease = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                lease.CancelAfter(RemoteDesktopProtocol.InputLeaseWindow);
                try
                {
                    payload = await ReceiveControlMessageAsync(socket, lease.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _ = _injector.ReleaseAll();
                    throw new TimeoutException("远程桌面输入心跳超时，已释放所有按键。", new OperationCanceledException());
                }
            }
            if (payload is null) break;
            if (Stopwatch.GetElapsedTime(rateWindow) >= TimeSpan.FromSeconds(1))
            {
                rateWindow = Stopwatch.GetTimestamp();
                messagesInWindow = 0;
            }
            if (++messagesInWindow > RemoteDesktopProtocol.MaximumInputMessagesPerSecond)
                throw new InvalidDataException("远程桌面输入速率超过安全限制。");
            var message = JsonSerializer.Deserialize<RemoteDesktopInputMessage>(
                payload,
                RemoteDesktopProtocol.JsonOptions)
                ?? throw new InvalidDataException("远程桌面输入消息为空。");
            ApplyInput(message, displayBounds);
        }
    }

    private void ApplyInput(RemoteDesktopInputMessage message, Rectangle displayBounds)
    {
        switch (message.Type)
        {
            case "heartbeat":
                return;
            case "pointer":
                MovePointer(message, displayBounds, required: true);
                return;
            case "mouse-button":
                MovePointer(message, displayBounds, required: false);
                if (message.Button is not (>= 0 and <= 4) || message.Down is null)
                    throw new InvalidDataException("远程鼠标按键消息无效。");
                var button = message.Button.Value switch
                {
                    0 => MouseButton.Left,
                    1 => MouseButton.Middle,
                    2 => MouseButton.Right,
                    3 => MouseButton.X1,
                    4 => MouseButton.X2,
                    _ => throw new InvalidDataException("远程鼠标按键无效。")
                };
                EnsureSucceeded(
                    _injector.SendMouseButton(new MouseButtonInjection(
                        button,
                        message.Down.Value ? InputTransition.Down : InputTransition.Up)),
                    new PrivilegedBridgeInputEvent(
                        "mouse-button",
                        (int)button,
                        message.Down.Value ? 1 : 0));
                return;
            case "wheel":
                MovePointer(message, displayBounds, required: false);
                if (message.Delta is null or < -1200 or > 1200)
                    throw new InvalidDataException("远程滚轮增量无效。");
                EnsureSucceeded(
                    _injector.SendMouseWheel((short)message.Delta.Value),
                    new PrivilegedBridgeInputEvent("mouse-wheel", 0, message.Delta.Value));
                return;
            case "key":
                if (message.VirtualKey is not (>= 1 and <= 254) || message.Down is null)
                    throw new InvalidDataException("远程键盘消息无效。");
                EnsureSucceeded(
                    _injector.SendKeyboard(new KeyboardInjection(
                        (ushort)message.VirtualKey.Value,
                        0,
                        message.Down.Value ? InputTransition.Down : InputTransition.Up,
                        UseScanCode: false,
                        IsExtended: message.Extended == true)),
                    new PrivilegedBridgeInputEvent(
                        "keyboard",
                        message.VirtualKey.Value,
                        0,
                        (message.Down.Value ? 0 : 1) | (message.Extended == true ? 2 : 0)));
                return;
            case "release":
                _ = _injector.ReleaseAll();
                _ = _privilegedBridge.Release();
                return;
            default:
                throw new InvalidDataException("远程桌面输入类型无效。");
        }
    }

    private void MovePointer(RemoteDesktopInputMessage message, Rectangle displayBounds, bool required)
    {
        if (message.X is null || message.Y is null)
        {
            if (required) throw new InvalidDataException("远程鼠标坐标缺失。");
            return;
        }
        var normalized = RemoteDesktopPointerMapper.MapToNormalizedVirtualDesktop(
            message.X.Value,
            message.Y.Value,
            displayBounds,
            SystemInformation.VirtualScreen);
        EnsureSucceeded(
            _injector.SendMousePosition(normalized.X, normalized.Y),
            new PrivilegedBridgeInputEvent("mouse-position", normalized.X, normalized.Y));
    }

    private void EnsureSucceeded(InputInjectionResult result, PrivilegedBridgeInputEvent fallback)
    {
        if (result.Succeeded) return;
        var elevated = _privilegedBridge.Inject([fallback]);
        if (!elevated.Available || elevated.Succeeded != 1)
            throw new InvalidOperationException(
                elevated.Available
                    ? "Windows 安全桌面拒绝了远程输入。"
                    : $"Windows 拒绝远程输入，错误码 {result.ErrorCode}；可在管理员设置中安装 UAC 安全桌面组件。");
    }

    private RuntimePeer RequireTarget(string targetDeviceId)
    {
        if (!_peers.TryGet(targetDeviceId, out var target) || !target.Paired)
            throw new KeyNotFoundException("没有找到已配对的远程设备。");
        if (!target.Online) throw new InvalidOperationException("远程设备当前离线。");
        if (!target.Capabilities.Contains("remote-desktop", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("远程设备版本不支持远程桌面，请在两台电脑上安装同一新版。");
        return target;
    }

    private static IReadOnlyList<ScreenEntry> GetScreens() => Screen.AllScreens
        .OrderBy(static screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
        .Select(static (screen, index) => new ScreenEntry(
            screen,
            new RemoteDesktopDisplay(
                index,
                screen.DeviceName,
                screen.Bounds.Width,
                screen.Bounds.Height,
                screen.Bounds.Left,
                screen.Bounds.Top,
                screen.Primary)))
        .ToArray();

    private static async Task<byte[]?> ReceiveControlMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("远程桌面控制通道只接受文本消息。");
            if (stream.Length + result.Count > RemoteDesktopProtocol.MaximumControlMessageBytes)
                throw new InvalidDataException("远程桌面控制消息超过大小限制。");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return stream.ToArray();
        }
    }

    private static async Task PumpWebSocketAsync(
        WebSocket source,
        WebSocket destination,
        int maximumMessageBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        var messageBytes = 0;
        WebSocketMessageType? messageType = null;
        while (!cancellationToken.IsCancellationRequested &&
               source.State is WebSocketState.Open or WebSocketState.CloseReceived &&
               destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await destination.CloseOutputAsync(
                        source.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        source.CloseStatusDescription,
                        cancellationToken);
                }
                return;
            }
            messageType ??= result.MessageType;
            if (messageType != result.MessageType)
                throw new InvalidDataException("远程桌面 WebSocket 分片类型不一致。");
            messageBytes = checked(messageBytes + result.Count);
            if (messageBytes > maximumMessageBytes)
                throw new InvalidDataException("远程桌面 WebSocket 消息超过大小限制。");
            await destination.SendAsync(
                buffer.AsMemory(0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                cancellationToken);
            if (result.EndOfMessage)
            {
                messageBytes = 0;
                messageType = null;
            }
        }
    }

    private static async Task<Exception?> ObservePumpAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (WebSocketException) { return null; }
        catch (Exception exception) { return exception; }
    }

    private static void Rethrow(Exception? exception)
    {
        if (exception is not null) ExceptionDispatchInfo.Capture(exception).Throw();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ = _injector.ReleaseAll();
        _ = _privilegedBridge.Release();
        _injector.Dispose();
        _captureGate.Dispose();
    }

    private sealed record ScreenEntry(Screen Screen, RemoteDesktopDisplay Display);

    private sealed record RemoteDesktopError(string Error);
}
