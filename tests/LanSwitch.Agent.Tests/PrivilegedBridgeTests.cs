using DeskMesh.PrivilegedBridge;
using LanSwitch.Core.Privileged;
using LanSwitch.Windows.Input;

namespace LanSwitch.Agent.Tests;

public sealed class PrivilegedBridgeTests
{
    private const string OwnerSid = "S-1-5-21-100-200-300-400";

    [Fact]
    public void CommandLineRequiresExactlyOneModeAndValidatedIdentity()
    {
        var command = BridgeCommandLine.Parse([
            "--install", "--owner-sid", OwnerSid, "--instance", "private"
        ]);

        Assert.Equal(BridgeMode.Install, command.Mode);
        Assert.Equal(OwnerSid, command.OwnerSid);
        Assert.Throws<ArgumentException>(() => BridgeCommandLine.Parse([
            "--install", "--service", "--owner-sid", OwnerSid, "--instance", "private"
        ]));
        Assert.Throws<ArgumentException>(() => BridgeCommandLine.Parse([
            "--install", "--owner-sid", "not-a-sid", "--instance", "private"
        ]));
        Assert.Throws<ArgumentException>(() => BridgeCommandLine.Parse([
            "--install", "--owner-sid", OwnerSid, "--instance", "../escape"
        ]));
    }

    [Fact]
    public async Task ContractRoundTripsLengthPrefixedJson()
    {
        var request = new PrivilegedBridgeRequest(
            PrivilegedBridgeContract.Version,
            PrivilegedBridgeContract.InjectOperation,
            [new PrivilegedBridgeInputEvent("keyboard", 65, 30, 0, 123)],
            AllowLockedSessionControl: true);
        await using var stream = new MemoryStream();

        await PrivilegedBridgeContract.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        var restored = await PrivilegedBridgeContract.ReadAsync<PrivilegedBridgeRequest>(
            stream,
            CancellationToken.None);

        Assert.Equal(request.Version, restored.Version);
        Assert.Equal(request.Operation, restored.Operation);
        Assert.Equal(request.Events, restored.Events);
        Assert.True(restored.AllowLockedSessionControl);
    }

    [Fact]
    public void HelperRejectsUnknownProtocolBeforeInjection()
    {
        using var injector = new RecordingInjector();
        injector.Start();

        var response = PrivilegedSessionHelper.HandleRequest(
            injector,
            new PrivilegedBridgeRequest(
                PrivilegedBridgeContract.Version + 1,
                PrivilegedBridgeContract.InjectOperation,
                [new PrivilegedBridgeInputEvent("keyboard", 65, 30)]));

        Assert.False(response.Available);
        Assert.Equal(0, injector.InjectionAttempts);
    }

    [Fact]
    public void HelperReleaseIsIdempotentAndDoesNotInject()
    {
        using var injector = new RecordingInjector();
        injector.Start();

        var first = PrivilegedSessionHelper.HandleRequest(
            injector,
            new PrivilegedBridgeRequest(
                PrivilegedBridgeContract.Version,
                PrivilegedBridgeContract.ReleaseOperation));
        var second = PrivilegedSessionHelper.HandleRequest(
            injector,
            new PrivilegedBridgeRequest(
                PrivilegedBridgeContract.Version,
                PrivilegedBridgeContract.ReleaseOperation));

        Assert.True(first.Available);
        Assert.True(second.Available);
        Assert.Equal(2, injector.ReleaseCalls);
        Assert.Equal(0, injector.InjectionAttempts);
    }

    [Fact]
    public void InstalledPathIsIsolatedPerOwnerAndInstance()
    {
        var first = PrivilegedBridgeInstaller.GetInstalledExecutablePath(OwnerSid, "private");
        var second = PrivilegedBridgeInstaller.GetInstalledExecutablePath(OwnerSid, "secondary");

        Assert.NotEqual(first, second);
        Assert.EndsWith("DeskMesh.PrivilegedBridge.exe", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PrivilegedBridgeContract.GetInstanceSuffix(OwnerSid, "private"), first);
    }

    [Theory]
    [InlineData("Winlogon", true, true, false, "uac-consent")]
    [InlineData("Winlogon", true, false, false, "uac-consent")]
    [InlineData("Winlogon", false, true, true, "locked-session")]
    [InlineData("Winlogon", false, true, false, null)]
    [InlineData("Winlogon", false, false, true, null)]
    [InlineData("Default", true, true, true, null)]
    [InlineData(null, true, true, true, null)]
    public void HelperSeparatesUacFromOptInLockedSessionControl(
        string? desktopName,
        bool consentUiActive,
        bool logonUiActive,
        bool allowLockedSessionControl,
        string? expected)
    {
        Assert.Equal(
            expected,
            PrivilegedSessionHelper.ClassifyDesktop(
                desktopName,
                consentUiActive,
                logonUiActive,
                allowLockedSessionControl));
    }

    [Fact]
    public void ServiceOnlyAcceptsExplicitEmptySecureAttentionRequest()
    {
        var allowed = new PrivilegedBridgeRequest(
            PrivilegedBridgeContract.Version,
            PrivilegedBridgeContract.SecureAttentionOperation,
            AllowLockedSessionControl: true);

        Assert.True(PrivilegedWindowsService.IsSecureAttentionRequestAllowed(allowed));
        Assert.False(PrivilegedWindowsService.IsSecureAttentionRequestAllowed(
            allowed with { AllowLockedSessionControl = false }));
        Assert.False(PrivilegedWindowsService.IsSecureAttentionRequestAllowed(
            allowed with { Events = [new PrivilegedBridgeInputEvent("keyboard", 17, 0)] }));
        Assert.False(PrivilegedWindowsService.IsSecureAttentionRequestAllowed(
            allowed with { Version = PrivilegedBridgeContract.Version - 1 }));
        Assert.False(PrivilegedWindowsService.IsSecureAttentionRequestAllowed(
            allowed with { Operation = PrivilegedBridgeContract.InjectOperation }));
    }

    private sealed class RecordingInjector : IWindowsInputInjector
    {
        public bool IsStarted { get; private set; }
        public int PressedInputCount => 0;
        public int InjectionAttempts { get; private set; }
        public int ReleaseCalls { get; private set; }

        public void Start() => IsStarted = true;
        public InputInjectionResult SendKeyboard(KeyboardInjection injection) => Inject();
        public InputInjectionResult SendMouseButton(MouseButtonInjection injection) => Inject();
        public InputInjectionResult SendMouseMove(int deltaX, int deltaY, bool absolute = false) => Inject();
        public InputInjectionResult SendMousePosition(int normalizedX, int normalizedY, bool virtualDesktop = true) => Inject();
        public InputInjectionResult SendMouseWheel(short delta, bool horizontal = false) => Inject();
        public InputReleaseResult ReleaseAll()
        {
            ReleaseCalls++;
            return new InputReleaseResult(0, 0, 0, 0);
        }
        public InputReleaseResult Stop()
        {
            IsStarted = false;
            return ReleaseAll();
        }
        public void Dispose() { }

        private InputInjectionResult Inject()
        {
            InjectionAttempts++;
            return new InputInjectionResult(1, 1, 0);
        }
    }
}
