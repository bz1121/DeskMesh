using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;
using LanSwitch.Windows.Display;

namespace LanSwitch.Agent.Tests;

public sealed class DisplayDepartureSafetyTests
{
    [Fact]
    public async Task NormalDepartureFreshProbesBeforeEveryWrite()
    {
        using var context = TestContext.Create();
        await context.ConfigureMappingsAsync();
        var probes = 0;
        var writes = 0;
        context.Display.ProbeRequested += _ =>
        {
            probes++;
            return Task.FromResult<IReadOnlyList<DisplayProbeView>>([Monitor(0x07)]);
        };
        context.Display.SwitchRequested += (monitorId, value, _) =>
        {
            writes++;
            Assert.Equal(MonitorId, monitorId);
            Assert.Equal(0x11u, value);
            return Task.FromResult(new DisplayOperationResult(
                true, true, false, "issued", CommandIssued: true));
        };

        var first = await context.Display.SwitchToDeviceAsync(RemoteDeviceId, CancellationToken.None);
        var second = await context.Display.SwitchToDeviceAsync(RemoteDeviceId, CancellationToken.None);

        Assert.True(first.FocusMayProceed);
        Assert.True(second.FocusMayProceed);
        Assert.Equal(2, probes);
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task NormalDepartureRejectsAStableInputThatDoesNotMatchTheSelfMapping()
    {
        using var context = TestContext.Create();
        await context.ConfigureMappingsAsync();
        var writes = 0;
        context.Display.ProbeRequested += _ => Task.FromResult<IReadOnlyList<DisplayProbeView>>([Monitor(0x11)]);
        context.Display.SwitchRequested += (_, _, _) =>
        {
            writes++;
            return Task.FromResult(new DisplayOperationResult(true, true, true, "unexpected"));
        };

        var result = await context.Display.SwitchToDeviceAsync(RemoteDeviceId, CancellationToken.None);

        Assert.True(result.Attempted);
        Assert.False(result.FocusMayProceed);
        Assert.False(result.CommandIssued);
        Assert.Equal(0, writes);
        Assert.Contains("与本机映射", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalDepartureRejectsMissingSelfMappingBeforeProbeOrWrite()
    {
        using var context = TestContext.Create();
        await context.Settings.UpdateAsync(current => current with
        {
            DisplayMappings = [new StoredDisplayMapping(MonitorId, RemoteDeviceId, "HDMI", 0x11)]
        });
        var probes = 0;
        var writes = 0;
        context.Display.ProbeRequested += _ =>
        {
            probes++;
            return Task.FromResult<IReadOnlyList<DisplayProbeView>>([Monitor(0x07)]);
        };
        context.Display.SwitchRequested += (_, _, _) =>
        {
            writes++;
            return Task.FromResult(new DisplayOperationResult(true, true, true, "unexpected"));
        };

        var result = await context.Display.SwitchToDeviceAsync(RemoteDeviceId, CancellationToken.None);

        Assert.True(result.Attempted);
        Assert.False(result.FocusMayProceed);
        Assert.Equal(0, probes);
        Assert.Equal(0, writes);
        Assert.Contains("缺少本机输入映射", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalArrivalAlwaysUsesAFreshProbeAndRequiresTheSelfValue()
    {
        using var context = TestContext.Create();
        await context.ConfigureMappingsAsync();
        var probes = 0;
        uint observed = 0x07;
        context.Display.ProbeRequested += _ =>
        {
            probes++;
            return Task.FromResult<IReadOnlyList<DisplayProbeView>>([Monitor(observed)]);
        };

        var confirmed = await context.Display.ConfirmLocalArrivalAsync(CancellationToken.None);
        observed = 0x11;
        var rejected = await context.Display.ConfirmLocalArrivalAsync(CancellationToken.None);

        Assert.True(confirmed.Confirmed);
        Assert.Equal(MonitorId, confirmed.MonitorId);
        Assert.Equal(0x07u, confirmed.ExpectedInput);
        Assert.Equal(0x07u, confirmed.ObservedInput);
        Assert.False(rejected.Confirmed);
        Assert.Equal(0x11u, rejected.ObservedInput);
        Assert.Equal(2, probes);
    }

    [Theory]
    [InlineData(DdcWriteStatus.AppliedWithUnexpectedValue)]
    [InlineData(DdcWriteStatus.AppliedWithUnstableSamples)]
    [InlineData(DdcWriteStatus.CommandIssuedUnverified)]
    public void AmbiguousNativeResultsPreserveThatACommandWasIssued(DdcWriteStatus status)
    {
        var native = new DdcWriteResult(status, null, 0x11, null, null, 0, "ambiguous");

        var translated = WindowsIntegrationService.TranslateNormalWriteResult(native);

        Assert.True(native.CommandWasIssued);
        Assert.True(translated.CommandIssued);
        Assert.False(translated.FocusMayProceed);
        Assert.False(translated.Verified);
        Assert.False(WindowsIntegrationService.CanReuseProbeAfterWrite(native));
    }

    [Theory]
    [InlineData(DdcWriteStatus.Verified)]
    [InlineData(DdcWriteStatus.AppliedButUnverified)]
    [InlineData(DdcWriteStatus.AppliedWithUnexpectedValue)]
    [InlineData(DdcWriteStatus.AppliedWithUnstableSamples)]
    [InlineData(DdcWriteStatus.CommandIssuedUnverified)]
    public void EveryIssuedNativeResultInvalidatesTheDepartureProbe(DdcWriteStatus status)
    {
        var native = new DdcWriteResult(status, null, 0x11, null, null, 0, null);

        Assert.True(native.CommandWasIssued);
        Assert.False(WindowsIntegrationService.CanReuseProbeAfterWrite(native));
    }

    [Fact]
    public void RejectedNativeResultDoesNotClaimThatACommandWasIssued()
    {
        var native = new DdcWriteResult(DdcWriteStatus.Rejected, null, 0x11, null, null, 5, "rejected");

        var translated = WindowsIntegrationService.TranslateNormalWriteResult(native);

        Assert.False(native.CommandWasIssued);
        Assert.False(translated.CommandIssued);
        Assert.False(translated.FocusMayProceed);
        Assert.True(WindowsIntegrationService.CanReuseProbeAfterWrite(native));
    }

    private static DisplayProbeView Monitor(uint currentInput) => new(
        MonitorId,
        "HKC G24H2",
        true,
        true,
        currentInput,
        currentInput == 0x07 ? "Desktop DP" : "Laptop HDMI",
        "fresh probe",
        true,
        []);

    private const string MonitorId = @"\\.\DISPLAY1|0|0123456789ABCDEF0123456789ABCDEF";
    private const string RemoteDeviceId = "remote-device";

    private sealed class TestContext : IDisposable
    {
        private readonly string _directory;

        private TestContext(
            string directory,
            DeviceIdentity identity,
            SettingsStore settings,
            DisplayCoordinator display)
        {
            _directory = directory;
            Identity = identity;
            Settings = settings;
            Display = display;
        }

        internal DeviceIdentity Identity { get; }
        internal SettingsStore Settings { get; }
        internal DisplayCoordinator Display { get; }

        internal static TestContext Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"LanSwitch-departure-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var options = AgentOptions.Parse(["--data-dir", directory]);
            var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
            var settings = new SettingsStore(options, identity);
            var state = new AppState(identity, settings, options);
            return new TestContext(directory, identity, settings, new DisplayCoordinator(settings, state));
        }

        internal Task<AgentSettings> ConfigureMappingsAsync() => Settings.UpdateAsync(current => current with
        {
            DisplayMappings =
            [
                new StoredDisplayMapping(MonitorId, Identity.DeviceId, "Desktop DP", 0x07),
                new StoredDisplayMapping(MonitorId, RemoteDeviceId, "Laptop HDMI", 0x11)
            ]
        });

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
