using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

public sealed class InputWorker : BackgroundService
{
    internal static readonly TimeSpan ConnectionTimeout = TimeSpan.FromMilliseconds(1750);

    private readonly DeviceIdentity _identity;
    private readonly InputCoordinator _input;
    private readonly AppState _state;
    private readonly ILogger<InputWorker> _logger;
    private readonly IInputWebSocketFactory _socketFactory;
    private readonly Func<OutgoingSessionSnapshot, RuntimePeer, Task<bool>> _failLocal;

    public InputWorker(
        DeviceIdentity identity,
        InputCoordinator input,
        AppState state,
        FocusCoordinator focus,
        ILogger<InputWorker> logger)
        : this(
            identity,
            input,
            state,
            logger,
            DefaultInputWebSocketFactory.Instance,
            (session, target) => Task.FromResult(
                focus.EmergencyReleaseIfOutgoingSessionCurrent(
                    session.Generation,
                    session.Epoch,
                    target.Id,
                    "输入通道中断，已恢复本机控制。")))
    {
    }

    internal InputWorker(
        DeviceIdentity identity,
        InputCoordinator input,
        AppState state,
        ILogger<InputWorker> logger,
        IInputWebSocketFactory socketFactory,
        Func<OutgoingSessionSnapshot, RuntimePeer, Task<bool>> failLocal)
    {
        _identity = identity;
        _input = input;
        _state = state;
        _logger = logger;
        _socketFactory = socketFactory;
        _failLocal = failLocal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IInputWebSocket? socket = null;
        CancellationTokenSource? socketLifetime = null;
        Task? socketMonitor = null;
        string? connectedPeer = null;
        long connectedGeneration = -1;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var session = _input.GetOutgoingSession();
                if (socket is not null && connectedGeneration != session.Generation)
                    ResetSocket(
                        ref socket,
                        ref socketLifetime,
                        ref socketMonitor,
                        ref connectedPeer,
                        ref connectedGeneration);

                using var sessionWait = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    session.Token);
                var target = session.Target;

                // Establish the low-latency channel as soon as focus is routed. Waiting for
                // the first mouse or keyboard packet made that packet pay the TLS handshake
                // cost and could incorrectly trigger the fail-local safety path.
                if (target is not null && !IsConnected(socket, connectedPeer, connectedGeneration, target, session))
                {
                    try
                    {
                        socket = await ConnectAsync(target, session, sessionWait.Token);
                        connectedPeer = target.Id;
                        connectedGeneration = session.Generation;
                        _state.AddDiagnostic("success", "输入通道",
                            $"WebSocket 已连接 {target.Name}，generation={session.Generation}，epoch={session.Epoch}。");
                        StartSocketMonitor(
                            socket,
                            session,
                            target,
                            stoppingToken,
                            ref socketLifetime,
                            ref socketMonitor);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException) when (sessionWait.IsCancellationRequested)
                    {
                        ResetSocket(
                            ref socket,
                            ref socketLifetime,
                            ref socketMonitor,
                            ref connectedPeer,
                            ref connectedGeneration);
                        continue;
                    }
                    catch (OperationCanceledException) when (!IsCurrentRemoteSession(session, target))
                    {
                        ResetSocket(
                            ref socket,
                            ref socketLifetime,
                            ref socketMonitor,
                            ref connectedPeer,
                            ref connectedGeneration);
                        continue;
                    }
                    catch (Exception exception)
                    {
                        ResetSocket(
                            ref socket,
                            ref socketLifetime,
                            ref socketMonitor,
                            ref connectedPeer,
                            ref connectedGeneration);
                        if (IsCurrentRemoteSession(session, target))
                            await FailLocalAsync(exception, session, target);
                        continue;
                    }
                }

                InputBatchPacket packet;
                try
                {
                    packet = await _input.Outgoing.ReadAsync(sessionWait.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException) when (sessionWait.IsCancellationRequested)
                {
                    ResetSocket(
                        ref socket,
                        ref socketLifetime,
                        ref socketMonitor,
                        ref connectedPeer,
                        ref connectedGeneration);
                    continue;
                }

                var current = _input.GetOutgoingSession();
                target = current.Target;
                if (current.Generation != session.Generation || target is null || packet.Epoch != current.Epoch)
                    continue;

                try
                {
                    if (!IsConnected(socket, connectedPeer, connectedGeneration, target, current))
                    {
                        ResetSocket(
                            ref socket,
                            ref socketLifetime,
                            ref socketMonitor,
                            ref connectedPeer,
                            ref connectedGeneration);
                        socket = await ConnectAsync(target, current, sessionWait.Token);
                        connectedPeer = target.Id;
                        connectedGeneration = current.Generation;
                        _state.AddDiagnostic("success", "输入通道",
                            $"WebSocket 已重连 {target.Name}，generation={current.Generation}，epoch={current.Epoch}。");
                        StartSocketMonitor(
                            socket,
                            current,
                            target,
                            stoppingToken,
                            ref socketLifetime,
                            ref socketMonitor);
                    }

                    var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
                    var activeSocket = socket ?? throw new InvalidOperationException("输入通道尚未建立。");
                    await activeSocket.SendAsync(bytes, WebSocketMessageType.Binary, true, sessionWait.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException) when (sessionWait.IsCancellationRequested)
                {
                    ResetSocket(
                        ref socket,
                        ref socketLifetime,
                        ref socketMonitor,
                        ref connectedPeer,
                        ref connectedGeneration);
                }
                catch (OperationCanceledException) when (!IsCurrentRemoteSession(current, target))
                {
                    ResetSocket(
                        ref socket,
                        ref socketLifetime,
                        ref socketMonitor,
                        ref connectedPeer,
                        ref connectedGeneration);
                }
                catch (Exception exception)
                {
                    ResetSocket(
                        ref socket,
                        ref socketLifetime,
                        ref socketMonitor,
                        ref connectedPeer,
                        ref connectedGeneration);
                    if (IsCurrentRemoteSession(current, target))
                        await FailLocalAsync(exception, current, target);
                }
            }
        }
        finally
        {
            ResetSocket(
                ref socket,
                ref socketLifetime,
                ref socketMonitor,
                ref connectedPeer,
                ref connectedGeneration);
        }
    }

    private void StartSocketMonitor(
        IInputWebSocket socket,
        OutgoingSessionSnapshot session,
        RuntimePeer target,
        CancellationToken stoppingToken,
        ref CancellationTokenSource? socketLifetime,
        ref Task? socketMonitor)
    {
        socketLifetime?.Cancel();
        socketLifetime?.Dispose();
        socketLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, session.Token);
        socketMonitor = MonitorSocketAsync(socket, session, target, socketLifetime.Token);
    }

    private async Task MonitorSocketAsync(
        IInputWebSocket socket,
        OutgoingSessionSnapshot expectedSession,
        RuntimePeer expectedTarget,
        CancellationToken cancellationToken)
    {
        try
        {
            await socket.ReceiveUntilClosedAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested &&
                IsCurrentRemoteSession(expectedSession, expectedTarget))
            {
                _state.AddDiagnostic("error", "输入通道",
                    $"远端 {expectedTarget.Name} 主动关闭 WebSocket，准备安全回切。");
                await FailLocalAsync(
                    new WebSocketException("远端已关闭输入通道。"),
                    expectedSession,
                    expectedTarget);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentRemoteSession(expectedSession, expectedTarget))
            {
                _state.AddDiagnostic("error", "输入通道",
                    $"WebSocket 接收失败：{exception.GetType().Name}：{exception.Message}");
                await FailLocalAsync(exception, expectedSession, expectedTarget);
            }
        }
    }

    private async Task<IInputWebSocket> ConnectAsync(
        RuntimePeer target,
        OutgoingSessionSnapshot session,
        CancellationToken sessionToken)
    {
        var socket = _socketFactory.Create(_identity, target);
        try
        {
            var uri = new Uri(PeerHttpClientFactory.BuildBaseAddress(target.Address, target.Port), "peer/v1/input");
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            connectTimeout.CancelAfter(ConnectionTimeout);
            await socket.ConnectAsync(new UriBuilder(uri) { Scheme = "wss" }.Uri, connectTimeout.Token);

            var current = _input.GetOutgoingSession();
            if (current.Generation != session.Generation || current.Target?.Id != target.Id)
                throw new OperationCanceledException("输入目标在连接期间已切换。", sessionToken);
            sessionToken.ThrowIfCancellationRequested();
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task FailLocalAsync(
        Exception exception,
        OutgoingSessionSnapshot expectedSession,
        RuntimePeer expectedTarget)
    {
        try
        {
            if (!await _failLocal(expectedSession, expectedTarget))
            {
                _logger.LogDebug(exception, "忽略已失效输入会话的传输异常");
                return;
            }
            _state.AddDiagnostic("error", "输入通道",
                $"连接 {expectedTarget.Name} 中断并已恢复本机：{exception.GetType().Name}：{exception.Message}");
            _logger.LogWarning(exception, "输入转发连接中断");
            _state.Publish("notice", new { level = "error", message = "输入转发中断，已恢复本机控制" });
        }
        catch (Exception releaseException)
        {
            _logger.LogError(releaseException, "输入通道中断后恢复本机控制失败");
        }
    }

    private static bool IsConnected(
        IInputWebSocket? socket,
        string? connectedPeer,
        long connectedGeneration,
        RuntimePeer target,
        OutgoingSessionSnapshot session) =>
        socket?.State == WebSocketState.Open &&
        string.Equals(connectedPeer, target.Id, StringComparison.Ordinal) &&
        connectedGeneration == session.Generation;

    private bool IsCurrentRemoteSession(OutgoingSessionSnapshot expected, RuntimePeer expectedTarget)
    {
        var current = _input.GetOutgoingSession();
        return current.Generation == expected.Generation &&
            current.Epoch == expected.Epoch &&
            string.Equals(current.Target?.Id, expectedTarget.Id, StringComparison.Ordinal);
    }

    private static void ResetSocket(
        ref IInputWebSocket? socket,
        ref CancellationTokenSource? socketLifetime,
        ref Task? socketMonitor,
        ref string? connectedPeer,
        ref long connectedGeneration)
    {
        socketLifetime?.Cancel();
        socket?.Dispose();
        socketLifetime?.Dispose();
        socket = null;
        socketLifetime = null;
        socketMonitor = null;
        connectedPeer = null;
        connectedGeneration = -1;
    }
}

internal interface IInputWebSocketFactory
{
    IInputWebSocket Create(DeviceIdentity identity, RuntimePeer target);
}

internal interface IInputWebSocket : IDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    Task ReceiveUntilClosedAsync(CancellationToken cancellationToken);
    Task SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);
}

internal sealed class DefaultInputWebSocketFactory : IInputWebSocketFactory
{
    internal static readonly DefaultInputWebSocketFactory Instance = new();

    private DefaultInputWebSocketFactory()
    {
    }

    public IInputWebSocket Create(DeviceIdentity identity, RuntimePeer target) =>
        new ClientInputWebSocket(CreateClientSocket(identity, target));

    internal static ClientWebSocket CreateClientSocket(DeviceIdentity identity, RuntimePeer target)
    {
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.ClientCertificates = new X509CertificateCollection { identity.Certificate };
        // Peer availability is already checked by HeartbeatService every 500 ms.
        // An aggressive bidirectional PING/PONG deadline made an otherwise healthy
        // idle input socket abort after roughly two seconds on some Windows hosts.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = Timeout.InfiniteTimeSpan;
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null &&
            string.Equals(
                DeviceIdentityStore.Fingerprint(
                    certificate as X509Certificate2 ?? new X509Certificate2(certificate)),
                target.Fingerprint,
                StringComparison.OrdinalIgnoreCase);
        return socket;
    }
}

internal sealed class ClientInputWebSocket(ClientWebSocket socket) : IInputWebSocket
{
    public WebSocketState State => socket.State;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        socket.ConnectAsync(uri, cancellationToken);

    public async Task ReceiveUntilClosedAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return;
        }
    }

    public Task SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) =>
        socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken).AsTask();

    public void Dispose() => socket.Dispose();
}
