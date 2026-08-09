using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Tests;

public sealed class PeerDisplaySwitchTests
{
    [Fact]
    public async Task CommittedPeerCanSwitchOnlyToItsOwnLocallyMappedInputOnce()
    {
        using var context = TestContext.Create();
        await context.ConfigureSafeReturnPathAsync();
        string? usedMonitor = null;
        uint? usedInput = null;
        var writes = 0;
        context.Display.SwitchRequested += (monitor, input, _) =>
        {
            writes++;
            usedMonitor = monitor;
            usedInput = input;
            return Task.FromResult(new DisplayOperationResult(
                true, true, true, "switched", CommandIssued: true));
        };
        var releases = 0;
        context.Input.ReleaseRequested += () => releases++;
        Assert.True(context.Input.PrepareIncoming(42, context.Peer.Id));
        Assert.True(context.Input.CommitIncoming(42, context.Peer.Id));
        var command = context.Command(42);

        var result = await context.Focus.SwitchDisplayForPeerAsync(
            context.Peer, command, CancellationToken.None);
        var replay = await context.Focus.SwitchDisplayForPeerAsync(
            context.Peer, command, CancellationToken.None);

        Assert.True(result.CommandIssued);
        Assert.True(result.Verified);
        Assert.Equal(MonitorId, usedMonitor);
        Assert.Equal(0x0Fu, usedInput);
        Assert.Equal(1, writes);
        Assert.True(replay.CommandIssued);
        Assert.Equal(1, releases);
        Assert.False(context.Input.AcceptRemoteBatch(context.Peer.Id,
            new InputBatchPacket(42, [new InputEventPacket("keyboard", 65, 30, 0, 1)], 1)));
    }

    [Fact]
    public async Task WrongDeviceBindingDoesNotConsumeTheValidEpoch()
    {
        using var context = TestContext.Create();
        await context.ConfigureSafeReturnPathAsync();
        var writes = 0;
        context.Display.SwitchRequested += (_, _, _) =>
        {
            writes++;
            return Task.FromResult(new DisplayOperationResult(
                true, true, true, "switched", CommandIssued: true));
        };
        Assert.True(context.Input.PrepareIncoming(43, context.Peer.Id));
        Assert.True(context.Input.CommitIncoming(43, context.Peer.Id));

        var wrongTarget = context.Command(43) with { TargetDeviceId = "third-device" };
        var rejected = await context.Focus.SwitchDisplayForPeerAsync(
            context.Peer, wrongTarget, CancellationToken.None);
        var accepted = await context.Focus.SwitchDisplayForPeerAsync(
            context.Peer, context.Command(43), CancellationToken.None);

        Assert.False(rejected.CommandIssued);
        Assert.True(accepted.CommandIssued);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task MissingMappingFailsClosedAndReleasesTheIncomingSession()
    {
        using var context = TestContext.Create();
        await context.Settings.UpdateAsync(current => current with
        {
            DisplayMappings = [new StoredDisplayMapping(MonitorId, context.Identity.DeviceId, "HDMI", 0x11)]
        });
        var writes = 0;
        var releases = 0;
        context.Display.SwitchRequested += (_, _, _) =>
        {
            writes++;
            return Task.FromResult(new DisplayOperationResult(
                true, true, true, "unexpected", CommandIssued: true));
        };
        context.Input.ReleaseRequested += () => releases++;
        Assert.True(context.Input.PrepareIncoming(44, context.Peer.Id));
        Assert.True(context.Input.CommitIncoming(44, context.Peer.Id));

        var result = await context.Focus.SwitchDisplayForPeerAsync(
            context.Peer, context.Command(44), CancellationToken.None);

        Assert.False(result.CommandIssued);
        Assert.Equal(0, writes);
        Assert.Equal(1, releases);
        Assert.False(context.Input.TryBeginIncomingDisplaySwitch(44, context.Peer.Id));
    }

    [Fact]
    public async Task DdcFailureFailsClosedAndReleasesTheIncomingSession()
    {
        using var context = TestContext.Create();
        await context.ConfigureSafeReturnPathAsync();
        context.Display.SwitchRequested += (_, _, _) => Task.FromResult(
            new DisplayOperationResult(true, false, false, "DDC failed"));
        var releases = 0;
        context.Input.ReleaseRequested += () => releases++;
        Assert.True(context.Input.PrepareIncoming(45, context.Peer.Id));

        var result = await context.Focus.SwitchDisplayForPeerAsync(
            context.Peer, context.Command(45), CancellationToken.None);

        Assert.False(result.CommandIssued);
        Assert.Contains("DDC failed", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task PrepareRequiresReturnMappingsAndTargetArrivalUsesFreshSelfProbe()
    {
        using var context = TestContext.Create();
        var command = new FocusCommand(46, context.Peer.Id, context.Identity.DeviceId);

        var missing = context.Focus.PrepareRemote(command);
        Assert.False(missing.Ready);
        Assert.False(context.Input.TryBeginIncomingDisplaySwitch(46, context.Peer.Id));

        await context.ConfigureSafeReturnPathAsync();
        context.Display.SwitchRequested += (_, _, _) => Task.FromResult(
            new DisplayOperationResult(true, true, true, "switched", CommandIssued: true));
        var prepared = context.Focus.PrepareRemote(command with { Epoch = 47 });
        var arrival = await context.Focus.ConfirmArrivalForPeerAsync(
            context.Peer, command with { Epoch = 47 }, CancellationToken.None);

        Assert.True(prepared.Ready);
        Assert.True(prepared.ReturnDisplayReady);
        Assert.True(arrival.Confirmed);
        Assert.True(context.Focus.CommitRemote(command with { Epoch = 47 }));
    }

    [Fact]
    public async Task DuplicateReturnSharesOneWriteAndClosingBlocksANewerPrepare()
    {
        using var context = TestContext.Create();
        await context.ConfigureSafeReturnPathAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        context.Display.SwitchRequested += async (_, _, _) =>
        {
            writes++;
            entered.TrySetResult();
            await release.Task;
            return new DisplayOperationResult(true, true, false, "issued", CommandIssued: true);
        };
        Assert.True(context.Input.PrepareIncoming(48, context.Peer.Id));
        Assert.True(context.Input.CommitIncoming(48, context.Peer.Id));
        var command = context.Command(48);

        var first = context.Focus.SwitchDisplayForPeerAsync(context.Peer, command, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = context.Focus.SwitchDisplayForPeerAsync(context.Peer, command, CancellationToken.None);
        Assert.Same(first, duplicate);
        Assert.False(context.Input.PrepareIncoming(49, context.Peer.Id));
        release.TrySetResult();

        Assert.True((await first).CommandIssued);
        Assert.True((await duplicate).CommandIssued);
        Assert.Equal(1, writes);
        Assert.True(context.Input.PrepareIncoming(49, context.Peer.Id));
    }

    [Fact]
    public async Task PeerReturnRejectsWriteOnlyModeEvenWhenTheEpochWasPreparedEarlier()
    {
        using var context = TestContext.Create();
        await context.ConfigureSafeReturnPathAsync();
        Assert.True(context.Input.PrepareIncoming(50, context.Peer.Id));
        Assert.True(context.Input.CommitIncoming(50, context.Peer.Id));
        await context.Settings.UpdateAsync(current => current with
        {
            DdcWriteOnlyEnabled = true,
            DdcWriteOnlyMonitorId = MonitorId,
            DdcWriteOnlyConfirmedInputs = [0x0F, 0x11]
        });
        var normalWrites = 0;
        var compatibilityWrites = 0;
        context.Display.SwitchRequested += (_, _, _) =>
        {
            normalWrites++;
            return Task.FromResult(new DisplayOperationResult(
                true, true, true, "unexpected normal write", CommandIssued: true));
        };
        context.Display.CompatibilityWriteRequested += (_, _, _) =>
        {
            compatibilityWrites++;
            return Task.FromResult(new DisplayOperationResult(
                true, true, true, "unexpected compatibility write", true, true));
        };

        var result = await context.Focus.SwitchDisplayForPeerAsync(
            context.Peer, context.Command(50), CancellationToken.None);

        Assert.False(result.CommandIssued);
        Assert.Equal("not-ready", result.Status);
        Assert.Equal(0, normalWrites);
        Assert.Equal(0, compatibilityWrites);
    }

    [Fact]
    public async Task ArrivalConfirmationRetriesAFalseSampleWithinTheBoundedAttemptCount()
    {
        var attempts = 0;
        var result = await FocusCoordinator.RetryArrivalConfirmationAsync(
            _ => Task.FromResult(new PeerDisplayArrivalResponse(
                Confirmed: ++attempts >= 2,
                Epoch: 51,
                Message: attempts >= 2 ? "arrived" : "not active yet")),
            static response => response.Confirmed,
            static () => true,
            CancellationToken.None,
            maximumAttempts: 4,
            retryDelay: TimeSpan.Zero);

        Assert.True(result.Confirmed);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ForwardSwitchAcceptsArrivalAfterAThreeSecondActivationDelay()
    {
        using var context = TestContext.Create();
        await context.ConfigureSafeReturnPathAsync();
        await context.EnsurePeerOnlineAsync();
        context.Display.SwitchRequested += (_, _, _) => Task.FromResult(
            new DisplayOperationResult(true, true, true, "switched", CommandIssued: true));
        var handler = new FocusPeerHandler(elapsed => elapsed >= TimeSpan.FromSeconds(2.7));
        context.Focus.PeerClientFactoryOverride = _ => handler.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        var result = await context.Focus.SwitchAsync(context.Peer.Id, CancellationToken.None);

        Assert.True(result.IsRemote);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(7.5));
        Assert.True(handler.ArrivalAttempts > 4);
        Assert.Equal(1, handler.Commits);
        Assert.Equal(0, handler.Rollbacks);
    }

    [Fact]
    public async Task ForwardArrivalDeadlineUsesChineseTimeoutAndRollsBackDisplay()
    {
        using var context = TestContext.Create();
        await context.ConfigureSafeReturnPathAsync();
        await context.EnsurePeerOnlineAsync();
        context.Display.SwitchRequested += (_, _, _) => Task.FromResult(
            new DisplayOperationResult(true, true, true, "switched", CommandIssued: true));
        var handler = new FocusPeerHandler(static _ => false);
        context.Focus.PeerClientFactoryOverride = _ => handler.CreateClient();

        var error = await Assert.ThrowsAsync<FocusArrivalTimeoutException>(() =>
            context.Focus.SwitchAsync(context.Peer.Id, CancellationToken.None));

        Assert.Contains("画面到达", error.Message, StringComparison.Ordinal);
        Assert.Contains("超时", error.Message, StringComparison.Ordinal);
        Assert.False(context.State.Focus.IsRemote);
        Assert.Equal(context.Identity.DeviceId, context.State.Focus.ActiveDeviceId);
        Assert.Equal(1, handler.Rollbacks);
        Assert.Equal(1, handler.Aborts);
        Assert.Equal(0, handler.Commits);
    }

    [Fact]
    public async Task ExternalCancellationIsNotReclassifiedAsArrivalTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var operation = FocusCoordinator.RunWithArrivalDeadlineAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            },
            cancellation.Token,
            TimeSpan.FromSeconds(8));

        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.IsNotType<FocusArrivalTimeoutException>(error);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void StaleTransportFailureCannotReleaseANewerOutgoingFocusSession()
    {
        using var context = TestContext.Create();
        context.Input.RouteTo(context.Peer, 71);
        var stale = context.Input.GetOutgoingSession();
        var newer = context.Peer with { Id = Guid.NewGuid().ToString("N"), Name = "newer peer" };
        context.Input.RouteTo(newer, 72);
        context.State.SetFocus(new FocusView(72, newer.Id, newer.Name, "remote", true));

        var released = context.Focus.EmergencyReleaseIfOutgoingSessionCurrent(
            stale.Generation,
            stale.Epoch,
            stale.Target!.Id,
            "stale transport failure");

        Assert.False(released);
        Assert.Equal(newer.Id, context.Input.Target?.Id);
        Assert.Equal(newer.Id, context.State.Focus.ActiveDeviceId);
        Assert.True(context.State.Focus.IsRemote);
        context.Input.ReturnLocal();
    }

    private const string MonitorId = @"\\.\DISPLAY1|0|0123456789ABCDEF0123456789ABCDEF";

    private sealed class TestContext : IDisposable
    {
        private readonly string _directory;

        private TestContext(string directory, DeviceIdentity identity, RuntimePeer peer,
            SettingsStore settings, AppState state, PeerDirectory peers, InputCoordinator input,
            DisplayCoordinator display, FocusCoordinator focus)
        {
            _directory = directory;
            Identity = identity;
            Peer = peer;
            Settings = settings;
            State = state;
            Peers = peers;
            Input = input;
            Display = display;
            Focus = focus;
        }

        internal DeviceIdentity Identity { get; }
        internal RuntimePeer Peer { get; }
        internal SettingsStore Settings { get; }
        internal AppState State { get; }
        internal PeerDirectory Peers { get; }
        internal InputCoordinator Input { get; }
        internal DisplayCoordinator Display { get; }
        internal FocusCoordinator Focus { get; }

        internal PeerDisplaySwitchCommand Command(long epoch) =>
            new(epoch, Peer.Id, Identity.DeviceId, Peer.Id);

        internal async Task ConfigureSafeReturnPathAsync()
        {
            await Settings.UpdateAsync(current => current with
            {
                DisplayMappings =
                [
                    new StoredDisplayMapping(MonitorId, Identity.DeviceId, "HDMI", 0x11),
                    new StoredDisplayMapping(MonitorId, Peer.Id, "DP", 0x0F)
                ]
            });
            Display.ProbeRequested += _ => Task.FromResult<IReadOnlyList<DisplayProbeView>>
            ([
                new DisplayProbeView(MonitorId, "Test monitor", true, true, 0x11, "HDMI",
                    "fresh probe", true, [])
            ]);
        }

        internal async Task EnsurePeerOnlineAsync() =>
            await Peers.StorePairAsync(new StoredPeer(Peer.Id, Peer.Name, Peer.Address, Peer.Port,
                Peer.Fingerprint, "certificate", DateTimeOffset.UtcNow));

        internal static TestContext Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"LanSwitch-peer-display-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var options = AgentOptions.Parse(["--data-dir", directory]);
            var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
            var settings = new SettingsStore(options, identity);
            var state = new AppState(identity, settings, options);
            var peers = new PeerDirectory(settings);
            var input = new InputCoordinator();
            var display = new DisplayCoordinator(settings, state);
            var focus = new FocusCoordinator(identity, settings, peers, new PeerHttpClientFactory(identity),
                state, input, display);
            var peer = new RuntimePeer(Guid.NewGuid().ToString("N"), "DP device", "192.168.1.20", 45832,
                new string('A', 64), null, true, true, 1, DateTimeOffset.UtcNow,
                ["input", "display"]);
            return new TestContext(directory, identity, peer, settings, state, peers, input, display, focus);
        }

        public void Dispose()
        {
            var settingsPath = Path.Combine(_directory, "settings.json");
            var temporaryPath = settingsPath + ".new";
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            Directory.Delete(_directory, recursive: false);
        }
    }

    private sealed class FocusPeerHandler(Func<TimeSpan, bool> arrivalConfirmed) : HttpMessageHandler
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private int _arrivalAttempts;
        private int _commits;
        private int _rollbacks;
        private int _aborts;

        internal int ArrivalAttempts => Volatile.Read(ref _arrivalAttempts);
        internal int Commits => Volatile.Read(ref _commits);
        internal int Rollbacks => Volatile.Read(ref _rollbacks);
        internal int Aborts => Volatile.Read(ref _aborts);

        internal HttpClient CreateClient() => new(this, disposeHandler: false)
        {
            BaseAddress = new Uri("https://peer.test/")
        };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/focus/prepare", StringComparison.Ordinal))
            {
                var command = await request.Content!.ReadFromJsonAsync<FocusCommand>(cancellationToken);
                return Json(HttpStatusCode.OK,
                    new FocusPrepareResponse(true, command!.Epoch, true, "ready"));
            }
            if (path.EndsWith("/display/confirm-arrival", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _arrivalAttempts);
                var command = await request.Content!.ReadFromJsonAsync<FocusCommand>(cancellationToken);
                var confirmed = arrivalConfirmed(_elapsed.Elapsed);
                return Json(confirmed ? HttpStatusCode.OK : HttpStatusCode.Conflict,
                    new PeerDisplayArrivalResponse(confirmed, command!.Epoch,
                        confirmed ? "arrived" : "尚未到达"));
            }
            if (path.EndsWith("/focus/commit", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _commits);
                return Json(HttpStatusCode.OK, new { committed = true });
            }
            if (path.EndsWith("/display/switch", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _rollbacks);
                var command = await request.Content!.ReadFromJsonAsync<PeerDisplaySwitchCommand>(cancellationToken);
                return Json(HttpStatusCode.OK,
                    new PeerDisplaySwitchResponse(true, command!.Epoch, true, "verified", "rolled back"));
            }
            if (path.EndsWith("/focus/abort", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _aborts);
                return Json(HttpStatusCode.OK, new { aborted = true });
            }
            return Json(HttpStatusCode.NotFound, new { error = "unexpected test route" });
        }

        private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) => new(status)
        {
            Content = JsonContent.Create(value)
        };
    }
}
