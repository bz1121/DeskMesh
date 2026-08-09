using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanSwitch.Agent.Tests;

public sealed class InputWorkerTests
{
    [Fact]
    public void DefaultInputSocketExplicitlyDisablesProxyUse()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=LanSwitch Input Test", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
        var identity = new DeviceIdentity("local", certificate, DeviceIdentityStore.Fingerprint(certificate));

        using var socket = DefaultInputWebSocketFactory.CreateClientSocket(identity, Peer("remote"));

        Assert.Null(socket.Options.Proxy);
        Assert.Equal(TimeSpan.FromSeconds(15), socket.Options.KeepAliveInterval);
        Assert.Equal(Timeout.InfiniteTimeSpan, socket.Options.KeepAliveTimeout);
    }

    [Fact]
    public void InputConnectionTimeoutIsLongerThanTheOldOneSecondLimitButNeverExceedsTwoSeconds()
    {
        Assert.True(InputWorker.ConnectionTimeout > TimeSpan.FromSeconds(1));
        Assert.True(InputWorker.ConnectionTimeout <= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RemoteSessionPreconnectsBeforeTheFirstInputPacket()
    {
        using var context = new TestContext();
        var socket = new FakeInputWebSocket();
        var factory = new FakeInputWebSocketFactory(_ => socket);
        var failLocalCalls = 0;
        using var worker = context.CreateWorker(factory, (_, _) =>
        {
            Interlocked.Increment(ref failLocalCalls);
            return Task.FromResult(true);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await worker.StartAsync(timeout.Token);
        context.Input.RouteTo(Peer("remote"), 41);

        await socket.Connected.Task.WaitAsync(timeout.Token);
        await socket.ReceiveStarted.Task.WaitAsync(timeout.Token);
        Assert.Equal(0, socket.SendCount);
        Assert.Equal(0, Volatile.Read(ref failLocalCalls));

        await worker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task RemoteSocketCloseFailsLocalWithoutWaitingForAnInputPacket()
    {
        using var context = new TestContext();
        var allowClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new FakeInputWebSocket(receive: async cancellationToken =>
        {
            await allowClose.Task.WaitAsync(cancellationToken);
        });
        var failedLocal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = context.CreateWorker(
            new FakeInputWebSocketFactory(_ => socket),
            (_, _) =>
            {
                context.Input.ReturnLocal();
                failedLocal.TrySetResult();
                return Task.FromResult(true);
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await worker.StartAsync(timeout.Token);
        context.Input.RouteTo(Peer("remote"), 42);
        await socket.ReceiveStarted.Task.WaitAsync(timeout.Token);
        allowClose.TrySetResult();
        await failedLocal.Task.WaitAsync(timeout.Token);

        Assert.Null(context.Input.Target);
        Assert.Equal(0, socket.SendCount);
        await worker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task SessionCancellationDuringPreconnectDoesNotTriggerFailLocal()
    {
        using var context = new TestContext();
        var firstConnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        var factory = new FakeInputWebSocketFactory(_ =>
        {
            var number = Interlocked.Increment(ref created);
            return number == 1
                ? new FakeInputWebSocket(async cancellationToken =>
                {
                    firstConnectStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                })
                : new FakeInputWebSocket(_ =>
                {
                    secondConnected.TrySetResult();
                    return Task.CompletedTask;
                });
        });
        var failLocalCalls = 0;
        using var worker = context.CreateWorker(factory, (_, _) =>
        {
            Interlocked.Increment(ref failLocalCalls);
            return Task.FromResult(true);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var firstPeer = Peer("first");
        var secondPeer = Peer("second");

        await worker.StartAsync(timeout.Token);
        context.State.SetFocus(new FocusView(51, firstPeer.Id, firstPeer.Name, "remote", true));
        context.Input.RouteTo(firstPeer, 51);
        await firstConnectStarted.Task.WaitAsync(timeout.Token);

        context.State.SetFocus(new FocusView(52, secondPeer.Id, secondPeer.Name, "remote", true));
        context.Input.RouteTo(secondPeer, 52);
        await secondConnected.Task.WaitAsync(timeout.Token);

        Assert.Equal(0, Volatile.Read(ref failLocalCalls));
        Assert.True(context.State.Focus.IsRemote);
        Assert.Equal(secondPeer.Id, context.State.Focus.ActiveDeviceId);

        await worker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task DisplayReturnSupersedesAnInFlightPreconnectWithoutFailingANewerFocusOperation()
    {
        using var context = new TestContext();
        var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowConnectToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedLocal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new FakeInputWebSocket(async _ =>
        {
            connectStarted.TrySetResult();
            await allowConnectToFinish.Task;
        });
        var factory = new FakeInputWebSocketFactory(_ => socket);
        using var worker = context.CreateWorker(factory, (_, _) =>
        {
            failedLocal.TrySetResult();
            return Task.FromResult(true);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var peer = Peer("returning");

        await worker.StartAsync(timeout.Token);
        context.State.SetFocus(new FocusView(71, peer.Id, peer.Name, "remote", true));
        context.Input.RouteTo(peer, 71);
        await connectStarted.Task.WaitAsync(timeout.Token);

        var returning = context.Input.BeginOutgoingDisplayReturn();
        context.State.SetFocus(new FocusView(72, context.Identity.DeviceId, "LOCAL", "local", false));
        allowConnectToFinish.TrySetResult();
        await socket.Disposed.Task.WaitAsync(timeout.Token);

        var unexpectedFailure = await Task.WhenAny(failedLocal.Task, Task.Delay(150, timeout.Token));
        Assert.NotSame(failedLocal.Task, unexpectedFailure);
        Assert.Null(context.Input.Target);

        context.Input.CompleteOutgoingDisplayReturn(returning.Generation);
        await worker.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task GenuineConnectionTimeoutFailsLocalWithinTheBoundedDeadline()
    {
        using var context = new TestContext();
        var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedLocal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeInputWebSocketFactory(_ => new FakeInputWebSocket(async cancellationToken =>
        {
            connectStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));
        using var worker = context.CreateWorker(factory, (_, _) =>
        {
            context.Input.ReturnLocal();
            context.State.SetFocus(new FocusView(62, context.Identity.DeviceId, "LOCAL", "local", false));
            failedLocal.TrySetResult();
            return Task.FromResult(true);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var peer = Peer("timeout");
        context.State.SetFocus(new FocusView(61, peer.Id, peer.Name, "remote", true));
        var elapsed = Stopwatch.StartNew();

        await worker.StartAsync(timeout.Token);
        context.Input.RouteTo(peer, 61);
        await connectStarted.Task.WaitAsync(timeout.Token);
        await failedLocal.Task.WaitAsync(timeout.Token);

        Assert.False(context.State.Focus.IsRemote);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2.5), $"Fail-local took {elapsed.Elapsed}.");

        await worker.StopAsync(timeout.Token);
    }

    private static RuntimePeer Peer(string id) => new(
        id,
        $"Peer {id}",
        "192.168.1.20",
        45832,
        new string('A', 64),
        null,
        true,
        true,
        1,
        DateTimeOffset.UtcNow,
        ["input"]);

    private sealed class TestContext : IDisposable
    {
        private readonly string _dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"LanSwitch-input-worker-{Guid.NewGuid():N}");

        internal TestContext()
        {
            Directory.CreateDirectory(_dataDirectory);
            Identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
            var options = AgentOptions.Parse(["--data-dir", _dataDirectory]);
            var settings = new SettingsStore(options, Identity);
            State = new AppState(Identity, settings, options);
        }

        internal DeviceIdentity Identity { get; }
        internal InputCoordinator Input { get; } = new();
        internal AppState State { get; }

        internal InputWorker CreateWorker(
            IInputWebSocketFactory factory,
            Func<OutgoingSessionSnapshot, RuntimePeer, Task<bool>> failLocal) =>
            new(Identity, Input, State, NullLogger<InputWorker>.Instance, factory, failLocal);

        public void Dispose()
        {
            var settingsPath = Path.Combine(_dataDirectory, "settings.json");
            var temporaryPath = settingsPath + ".new";
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            Directory.Delete(_dataDirectory, recursive: false);
        }
    }

    private sealed class FakeInputWebSocketFactory(Func<RuntimePeer, FakeInputWebSocket> create)
        : IInputWebSocketFactory
    {
        private readonly ConcurrentQueue<FakeInputWebSocket> _created = new();

        public IInputWebSocket Create(DeviceIdentity identity, RuntimePeer target)
        {
            var socket = create(target);
            _created.Enqueue(socket);
            return socket;
        }
    }

    private sealed class FakeInputWebSocket : IInputWebSocket
    {
        private readonly Func<CancellationToken, Task> _connect;
        private readonly Func<CancellationToken, Task> _receive;
        private int _sendCount;

        internal FakeInputWebSocket(
            Func<CancellationToken, Task>? connect = null,
            Func<CancellationToken, Task>? receive = null)
        {
            _connect = connect ?? (_ => Task.CompletedTask);
            _receive = receive ?? (cancellationToken =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }

        internal TaskCompletionSource Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReceiveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int SendCount => Volatile.Read(ref _sendCount);
        public WebSocketState State { get; private set; } = WebSocketState.None;

        public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            State = WebSocketState.Connecting;
            await _connect(cancellationToken);
            State = WebSocketState.Open;
            Connected.TrySetResult();
        }

        public Task SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _sendCount);
            return Task.CompletedTask;
        }

        public async Task ReceiveUntilClosedAsync(CancellationToken cancellationToken)
        {
            ReceiveStarted.TrySetResult();
            await _receive(cancellationToken);
        }

        public void Dispose()
        {
            if (State is not WebSocketState.Closed and not WebSocketState.Aborted)
                State = WebSocketState.Closed;
            Disposed.TrySetResult();
        }
    }
}
