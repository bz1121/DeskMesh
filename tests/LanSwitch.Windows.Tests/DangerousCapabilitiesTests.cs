using LanSwitch.Windows.Clipboard;
using LanSwitch.Windows.Display;
using LanSwitch.Windows.Input;
using LanSwitch.Windows.Startup;

namespace LanSwitch.Windows.Tests;

public sealed class DangerousCapabilitiesTests
{
    [Fact]
    public void NativeListenersAndInjectionAreDisabledAtConstruction()
    {
        using var clipboard = new WindowsClipboardUpdateMonitor();
        using var hooks = new WindowsLowLevelInputHook();
        using var injector = new WindowsInputInjector();

        Assert.False(clipboard.IsRunning);
        Assert.False(hooks.IsRunning);
        Assert.False(hooks.IsHealthy);
        Assert.False(injector.IsStarted);
        Assert.Throws<InvalidOperationException>(() => injector.SendKeyboard(
            new KeyboardInjection(0x41, 0x1E, InputTransition.Down)));
        Assert.Throws<InvalidOperationException>(() => injector.SendMousePosition(32767, 32767));
    }

    [Fact]
    public async Task DdcReadAndWriteRequireASuccessfulProbe()
    {
        using var controller = new WindowsDdcMonitorController();

        var read = await controller.ReadInputSourceAsync("not-probed");
        var write = await controller.WriteInputSourceAsync("not-probed", 0x11);

        Assert.Equal(DdcReadStatus.NotProbed, read.Status);
        Assert.Equal(DdcWriteStatus.NotProbed, write.Status);
    }

    [Fact]
    public async Task WriteOnlyDdcRequiresOptInAndAnAllowedCandidateBeforeNativeEnumeration()
    {
        using var controller = new WindowsDdcMonitorController();
        var fingerprint = DdcMonitorIdentityRules.CreateTopologyFingerprint("monitor-interface");
        var monitor = new DdcMonitorIdentity(
            DdcMonitorIdentityRules.CreateMonitorId(@"\\.\DISPLAY1", 0, fingerprint),
            @"\\.\DISPLAY1",
            0,
            "Example monitor",
            fingerprint);

        var disabled = await controller.WriteInputSourceCompatibilityAsync(
            monitor,
            DdcKnownInputSources.Hdmi1);
        var unlisted = await controller.WriteInputSourceCompatibilityAsync(
            monitor,
            0x1B,
            new DdcWriteOnlyCompatibilityOptions { AcknowledgeUnverifiedWriteRisk = true });

        Assert.Equal(DdcWriteStatus.CompatibilityModeDisabled, disabled.Status);
        Assert.False(disabled.CommandWasIssued);
        Assert.Equal(DdcWriteStatus.InputSourceNotAllowed, unlisted.Status);
        Assert.False(unlisted.CommandWasIssued);
    }

    [Fact]
    public async Task CompatibilityTargetEnumerationHonorsCancellationBeforeNativeAccess()
    {
        using var controller = new WindowsDdcMonitorController();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.EnumerateCompatibilityTargetsAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public void AutoStartRejectsRelativePathsBeforeOpeningTheRegistry()
    {
        var manager = new HkcuAutoStartManager("LanSwitch.Tests");

        Assert.Throws<ArgumentException>(() => manager.Enable("LanSwitch.Agent.exe"));
    }

    [Fact]
    public void HookSuppressionIsOptIn()
    {
        var options = new InputHookOptions();

        Assert.False(options.AllowSuppression);
        Assert.True(options.IgnoreInjectedForHotkeys);
    }
}
