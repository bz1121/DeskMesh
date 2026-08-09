using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Tests;

public sealed class DisplayCompatibilityTests
{
    [Fact]
    public async Task WriteOnlyModeRequiresTwoConfirmedInputsAndTwoDeviceMappings()
    {
        using var context = TestContext.Create();
        context.Display.ProbeRequested += _ => Task.FromResult<IReadOnlyList<DisplayProbeView>>(
        [
            Monitor(writeOnlyEligible: true)
        ]);
        context.Display.CompatibilityWriteRequested += (_, _, _) => Task.FromResult(
            new DisplayOperationResult(true, true, false, "command sent", true, true));

        await context.Display.ProbeAsync(CancellationToken.None);
        await context.Display.SetWriteOnlyCompatibilityAsync(
            new DisplayCompatibilityRequest(true, MonitorId, true), CancellationToken.None);

        var blocked = await context.Display.SwitchToDeviceAsync(RemoteDeviceId, CancellationToken.None);
        Assert.False(blocked.FocusMayProceed);

        await TestAndConfirmAsync(context.Display, 0x11);
        Assert.Equal([0x11u], context.Settings.Snapshot.DdcWriteOnlyConfirmedInputs);
        Assert.False(context.State.Display.WriteOnlyAutomaticReady);

        await TestAndConfirmAsync(context.Display, 0x0F);
        await context.Display.SaveMappingAsync(
            new DisplayMappingRequest(MonitorId, context.Identity.DeviceId, "本机 DP", 0x0F),
            CancellationToken.None);
        await context.Display.SaveMappingAsync(
            new DisplayMappingRequest(MonitorId, RemoteDeviceId, "远端 HDMI1", 0x11),
            CancellationToken.None);

        Assert.True(context.State.Display.WriteOnlyAutomaticReady);
        var remote = await context.Display.SwitchToDeviceAsync(RemoteDeviceId, CancellationToken.None);
        var local = await context.Display.SwitchToDeviceAsync(context.Identity.DeviceId, CancellationToken.None);
        Assert.True(remote.FocusMayProceed);
        Assert.True(local.FocusMayProceed);
        Assert.True(remote.CommandIssued);
        Assert.True(local.CommandIssued);
    }

    [Fact]
    public async Task TestMustBeVisuallyConfirmedBeforeMappingCanBeSaved()
    {
        using var context = TestContext.Create();
        context.Display.ProbeRequested += _ => Task.FromResult<IReadOnlyList<DisplayProbeView>>(
        [
            Monitor(writeOnlyEligible: true)
        ]);
        context.Display.CompatibilityWriteRequested += (_, _, _) => Task.FromResult(
            new DisplayOperationResult(true, true, false, "command sent", true, true));
        await context.Display.ProbeAsync(CancellationToken.None);
        await context.Display.SetWriteOnlyCompatibilityAsync(
            new DisplayCompatibilityRequest(true, MonitorId, true), CancellationToken.None);

        var pending = await context.Display.TestWriteOnlyAsync(
            new DisplayCompatibilityTestRequest(MonitorId, 0x11, true), CancellationToken.None);
        Assert.NotNull(pending.ConfirmationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Display.SaveMappingAsync(
            new DisplayMappingRequest(MonitorId, RemoteDeviceId, "远端 HDMI1", 0x11),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Display.ConfirmWriteOnlyTestAsync(
            new DisplayCompatibilityTestConfirmation(
                Guid.NewGuid().ToString("N"), MonitorId, 0x11, true),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Display.TestWriteOnlyAsync(
            new DisplayCompatibilityTestRequest(MonitorId, 0x0F, true), CancellationToken.None));

        await context.Display.ConfirmWriteOnlyTestAsync(
            new DisplayCompatibilityTestConfirmation(
                pending.ConfirmationId!, MonitorId, 0x11, true),
            CancellationToken.None);
        await context.Display.SaveMappingAsync(
            new DisplayMappingRequest(MonitorId, RemoteDeviceId, "远端 HDMI1", 0x11),
            CancellationToken.None);
    }

    [Fact]
    public async Task IdentityChangeClearsWriteOnlyAuthorizationAndStaleDisplayValues()
    {
        using var context = TestContext.Create();
        IReadOnlyList<DisplayProbeView> probes =
        [
            Monitor(writeOnlyEligible: true, stable: true, currentInput: 0x11)
        ];
        context.Display.ProbeRequested += _ => Task.FromResult(probes);
        context.Display.CompatibilityWriteRequested += (_, _, _) => Task.FromResult(
            new DisplayOperationResult(true, true, false, "command sent", true, true));
        await context.Display.ProbeAsync(CancellationToken.None);
        await context.Display.SetWriteOnlyCompatibilityAsync(
            new DisplayCompatibilityRequest(true, MonitorId, true), CancellationToken.None);
        await TestAndConfirmAsync(context.Display, 0x11);

        Assert.False(context.Display.Snapshot().Single().Stable);
        Assert.Null(context.Display.Snapshot().Single().CurrentInput);
        probes = [];
        await context.Display.ProbeAsync(CancellationToken.None);

        Assert.False(context.Settings.Snapshot.DdcWriteOnlyEnabled);
        Assert.Empty(context.Settings.Snapshot.DdcWriteOnlyConfirmedInputs ?? []);
        Assert.False(context.State.Display.WriteOnlyEnabled);
        Assert.Empty(context.State.Display.WriteOnlyConfirmedInputs);
    }

    [Theory]
    [InlineData(0x0F, true)]
    [InlineData(0x11, true)]
    [InlineData(0x12, false)]
    [InlineData(0x01, false)]
    public void CompatibilityInputAllowlistIsFixed(uint value, bool expected)
    {
        Assert.Equal(expected, DisplayCoordinator.IsWriteOnlyInputValue(value));
    }

    [Fact]
    public async Task PersistedCompleteCalibrationRestoresReadyStateButNeverPhysicalFollow()
    {
        using var context = TestContext.Create();
        await context.Settings.UpdateAsync(current => current with
        {
            DdcWriteOnlyEnabled = true,
            DdcWriteOnlyMonitorId = MonitorId,
            DdcWriteOnlyConfirmedInputs = [0x0F, 0x11],
            PhysicalFollowEnabled = true,
            DisplayMappings =
            [
                new StoredDisplayMapping(MonitorId, context.Identity.DeviceId, "本机 DP", 0x0F),
                new StoredDisplayMapping(MonitorId, RemoteDeviceId, "远端 HDMI1", 0x11)
            ]
        });

        var reloadedSettings = new SettingsStore(context.Options, context.Identity);
        var reloadedState = new AppState(context.Identity, reloadedSettings, context.Options);

        Assert.False(reloadedSettings.Snapshot.PhysicalFollowEnabled);
        Assert.True(reloadedState.Display.WriteOnlyAutomaticReady);
    }

    [Fact]
    public async Task ConcurrentTestsReserveTheSingleVisualConfirmationSlotBeforeWriting()
    {
        using var context = TestContext.Create();
        context.Display.ProbeRequested += _ => Task.FromResult<IReadOnlyList<DisplayProbeView>>([Monitor(true)]);
        await context.Display.ProbeAsync(CancellationToken.None);
        await context.Display.SetWriteOnlyCompatibilityAsync(
            new DisplayCompatibilityRequest(true, MonitorId, true), CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        context.Display.CompatibilityWriteRequested += async (_, _, _) =>
        {
            Interlocked.Increment(ref writes);
            entered.TrySetResult();
            await release.Task;
            return new DisplayOperationResult(true, true, false, "command sent", true, true);
        };

        var first = context.Display.TestWriteOnlyAsync(
            new DisplayCompatibilityTestRequest(MonitorId, 0x11, true), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = context.Display.TestWriteOnlyAsync(
            new DisplayCompatibilityTestRequest(MonitorId, 0x0F, true), CancellationToken.None);
        release.TrySetResult();

        Assert.True((await first).CommandIssued);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(1, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task DisablingCompatibilityWaitsForAnInFlightWriteThenClearsItsConfirmation()
    {
        using var context = TestContext.Create();
        context.Display.ProbeRequested += _ => Task.FromResult<IReadOnlyList<DisplayProbeView>>([Monitor(true)]);
        await context.Display.ProbeAsync(CancellationToken.None);
        await context.Display.SetWriteOnlyCompatibilityAsync(
            new DisplayCompatibilityRequest(true, MonitorId, true), CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Display.CompatibilityWriteRequested += async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new DisplayOperationResult(true, true, false, "command sent", true, true);
        };

        var test = context.Display.TestWriteOnlyAsync(
            new DisplayCompatibilityTestRequest(MonitorId, 0x11, true), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disable = context.Display.SetWriteOnlyCompatibilityAsync(
            new DisplayCompatibilityRequest(false, MonitorId, false), CancellationToken.None);
        Assert.False(disable.IsCompleted);
        release.TrySetResult();

        var testResult = await test;
        Assert.True(testResult.CommandIssued);
        var disabled = await disable;
        Assert.False(disabled.WriteOnlyEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Display.ConfirmWriteOnlyTestAsync(
            new DisplayCompatibilityTestConfirmation(
                testResult.ConfirmationId!, MonitorId, 0x11, true), CancellationToken.None));
    }

    [Fact]
    public async Task EnablingWriteOnlyWaitsForAnInFlightNormalProbe()
    {
        using var context = TestContext.Create();
        var shouldBlock = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Display.ProbeRequested += async _ =>
        {
            if (shouldBlock)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return [Monitor(true, stable: true, currentInput: 0x11)];
        };
        await context.Display.ProbeAsync(CancellationToken.None);
        shouldBlock = true;

        var probe = context.Display.ProbeAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var enable = context.Display.SetWriteOnlyCompatibilityAsync(
            new DisplayCompatibilityRequest(true, MonitorId, true), CancellationToken.None);
        Assert.False(enable.IsCompleted);
        release.TrySetResult();

        await probe;
        var enabled = await enable;
        Assert.True(enabled.WriteOnlyEnabled);
        Assert.False(enabled.Stable);
        Assert.Null(enabled.CurrentInput);
    }

    [Fact]
    public async Task PhysicalFollowLeaseBlocksWriteOnlyEnableUntilTheAuthorizedTransitionFinishes()
    {
        using var context = TestContext.Create();
        context.Display.ProbeRequested += _ => Task.FromResult<IReadOnlyList<DisplayProbeView>>(
            [Monitor(true, stable: true, currentInput: 0x11)]);
        await context.Display.ProbeAsync(CancellationToken.None);
        await context.Display.SaveMappingAsync(
            new DisplayMappingRequest(MonitorId, context.Identity.DeviceId, "本机 DP", 0x0F),
            CancellationToken.None);
        await context.Display.SaveMappingAsync(
            new DisplayMappingRequest(MonitorId, RemoteDeviceId, "远端 HDMI1", 0x11),
            CancellationToken.None);
        await context.PairRemoteAsync();
        await context.Display.SetPhysicalFollowAsync(true, CancellationToken.None);
        var observationPermit = await context.Display.TryCreatePhysicalFollowObservationPermitAsync(
            RemoteDeviceId, CancellationToken.None);
        Assert.NotNull(observationPermit);
        var followLease = await context.Display.TryAcquirePhysicalFollowLeaseAsync(
            RemoteDeviceId, 0x11, CancellationToken.None);
        Assert.NotNull(followLease);
        var enable = context.Display.SetWriteOnlyCompatibilityAsync(
            new DisplayCompatibilityRequest(true, MonitorId, true), CancellationToken.None);
        Assert.False(enable.IsCompleted);
        followLease.Dispose();

        var enabled = await enable;
        Assert.True(enabled.WriteOnlyEnabled);
        Assert.True(observationPermit.ConfigurationToken.IsCancellationRequested);
        Assert.False(enabled.PhysicalFollowEnabled);
        Assert.False(context.Settings.Snapshot.PhysicalFollowEnabled);
    }

    [Fact]
    public async Task PublisherPermitCancelsOnModeChangeWithoutHoldingTheModeGateAcrossNetworkWork()
    {
        using var context = TestContext.Create();
        context.Display.ProbeRequested += _ => Task.FromResult<IReadOnlyList<DisplayProbeView>>(
            [Monitor(true, stable: true, currentInput: 0x0F)]);
        context.Display.SwitchRequested += (_, _, _) => Task.FromResult(
            new DisplayOperationResult(true, true, true, "ready", CommandIssued: true));
        await context.Display.ProbeAsync(CancellationToken.None);
        await context.Display.SaveMappingAsync(
            new DisplayMappingRequest(MonitorId, context.Identity.DeviceId, "Local DP", 0x0F),
            CancellationToken.None);
        await context.Display.SaveMappingAsync(
            new DisplayMappingRequest(MonitorId, RemoteDeviceId, "Remote HDMI1", 0x11),
            CancellationToken.None);
        await context.PairRemoteAsync();

        var permit = await context.Display.TryAcquirePhysicalFollowPublisherLeaseAsync(
            RemoteDeviceId, MonitorId, 0x0F, CancellationToken.None);
        Assert.NotNull(permit);
        Assert.False(permit.ConfigurationToken.IsCancellationRequested);

        var enabled = await context.Display.SetWriteOnlyCompatibilityAsync(
            new DisplayCompatibilityRequest(true, MonitorId, true), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(enabled.WriteOnlyEnabled);
        Assert.True(permit.ConfigurationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ReadableModeKeepsDistributedFollowEnabledWhenLocalDdcDisappears()
    {
        using var context = TestContext.Create();
        var readable = true;
        context.Display.ProbeRequested += _ => Task.FromResult<IReadOnlyList<DisplayProbeView>>(
            [Monitor(true, stable: readable, currentInput: readable ? 0x0F : null)]);
        await context.Display.ProbeAsync(CancellationToken.None);
        await context.Display.SaveMappingAsync(
            new DisplayMappingRequest(MonitorId, context.Identity.DeviceId, "本机 DP", 0x0F),
            CancellationToken.None);
        await context.Display.SaveMappingAsync(
            new DisplayMappingRequest(MonitorId, RemoteDeviceId, "远端 HDMI1", 0x11),
            CancellationToken.None);
        await context.PairRemoteAsync();
        await context.Display.SetPhysicalFollowAsync(true, CancellationToken.None);

        readable = false;
        await context.Display.ProbeAsync(CancellationToken.None);

        Assert.True(context.Settings.Snapshot.PhysicalFollowEnabled);
        Assert.True(context.State.Display.PhysicalFollowEnabled);
        Assert.Contains("等待远端 Agent", context.State.Display.Message, StringComparison.Ordinal);
    }

    private static async Task TestAndConfirmAsync(DisplayCoordinator display, uint value)
    {
        var result = await display.TestWriteOnlyAsync(
            new DisplayCompatibilityTestRequest(MonitorId, value, true), CancellationToken.None);
        Assert.True(result.CommandIssued);
        Assert.NotNull(result.ConfirmationId);
        await display.ConfirmWriteOnlyTestAsync(
            new DisplayCompatibilityTestConfirmation(result.ConfirmationId!, MonitorId, value, true),
            CancellationToken.None);
    }

    private static DisplayProbeView Monitor(
        bool writeOnlyEligible,
        bool stable = false,
        uint? currentInput = null) => new(
        MonitorId,
        "HKC G24H2",
        stable,
        stable,
        currentInput,
        currentInput == 0x11 ? "HDMI1" : null,
        "probe",
        writeOnlyEligible,
        []);

    private const string MonitorId = @"\\.\DISPLAY1|0|0123456789ABCDEF0123456789ABCDEF";
    private const string RemoteDeviceId = "remote-device";

    private sealed class TestContext : IDisposable
    {
        private readonly string _directory;

        private TestContext(string directory, AgentOptions options, DeviceIdentity identity, SettingsStore settings,
            AppState state, DisplayCoordinator display)
        {
            _directory = directory;
            Options = options;
            Identity = identity;
            Settings = settings;
            State = state;
            Display = display;
        }

        internal DeviceIdentity Identity { get; }
        internal AgentOptions Options { get; }
        internal SettingsStore Settings { get; }
        internal AppState State { get; }
        internal DisplayCoordinator Display { get; }

        internal Task PairRemoteAsync() => Settings.UpdateAsync(current => current with
        {
            Peers =
            [
                new StoredPeer(RemoteDeviceId, "Remote", "192.168.1.2", 45832,
                    new string('A', 64), "certificate", DateTimeOffset.UtcNow)
            ]
        });

        internal static TestContext Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"LanSwitch-display-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var options = AgentOptions.Parse(["--data-dir", directory]);
            var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
            var settings = new SettingsStore(options, identity);
            var state = new AppState(identity, settings, options);
            return new TestContext(directory, options, identity, settings, state,
                new DisplayCoordinator(settings, state));
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
}
