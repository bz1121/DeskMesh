using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;
using LanSwitch.Agent.Tray;

namespace LanSwitch.Agent.Tests;

public sealed class AgentBoundaryTests
{
    [Fact]
    public void MainKeepsWinFormsMessageLoopOnTheStaEntryThread()
    {
        var main = typeof(LanSwitch.Agent.Program).GetMethod(
            "Main",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(main);
        Assert.Equal(typeof(int), main.ReturnType);
        Assert.NotNull(main.GetCustomAttribute<STAThreadAttribute>());
        Assert.Null(main.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Fact]
    public void DefaultLocalConsolePortIs5616()
    {
        var options = AgentOptions.Parse([]);

        Assert.Equal(5616, options.WebPort);
        Assert.Equal(45832, options.PeerPort);
    }

    [Fact]
    public void NewInstallKeepsAutomaticSensitiveFeaturesDisabled()
    {
        var defaults = AgentSettings.CreateDefault("local");

        Assert.False(defaults.ClipboardEnabled);
        Assert.False(defaults.ClipboardTextEnabled);
        Assert.False(defaults.ClipboardImageEnabled);
        Assert.False(defaults.AudioForwardingEnabled);
        Assert.False(defaults.RemoteDesktopEnabled);
    }

    [Fact]
    public void PeerTransportAllowsOnlyTls12AndTls13()
    {
        Assert.Equal(
            System.Security.Authentication.SslProtocols.Tls12 |
            System.Security.Authentication.SslProtocols.Tls13,
            LanSwitch.Agent.Program.AllowedPeerTlsProtocols);
    }

    [Fact]
    public void ExceptionBoundaryLocalizesArrivalTimeoutAndCancellation()
    {
        var timeout = ApiEndpoints.ClassifyException(
            new FocusArrivalTimeoutException("等待画面到达已超时。"), requestAborted: false);
        var emergency = ApiEndpoints.ClassifyException(
            new OperationCanceledException("The operation was canceled."), requestAborted: false);
        var disconnected = ApiEndpoints.ClassifyException(
            new OperationCanceledException("The operation was canceled."), requestAborted: true);

        Assert.Equal(504, timeout.StatusCode);
        Assert.Equal("等待画面到达已超时。", timeout.Detail);
        Assert.Equal(409, emergency.StatusCode);
        Assert.Equal("操作已取消，或已被紧急回切取代。", emergency.Detail);
        Assert.Equal(499, disconnected.StatusCode);
        Assert.Null(disconnected.Detail);
    }

    [Fact]
    public void ExceptionBoundaryDoesNotExposeUnexpectedExceptionDetails()
    {
        var result = ApiEndpoints.ClassifyException(
            new Exception(@"secret path C:\\Users\\example\\identity.bin"), requestAborted: false);

        Assert.Equal(500, result.StatusCode);
        Assert.NotNull(result.Detail);
        Assert.DoesNotContain("secret", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("identity.bin", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("traceId", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitPortsOverrideDefaults()
    {
        var options = AgentOptions.Parse(["--web-port", "6616", "--peer-port", "6617"]);

        Assert.Equal(6616, options.WebPort);
        Assert.Equal(6617, options.PeerPort);
    }

    [Fact]
    public void DataDirectoryIgnoresTrailingDirectorySeparator()
    {
        var path = Path.Combine(Path.GetTempPath(), "LanSwitch-options-test");

        var plain = AgentOptions.Parse(["--data-dir", path]);
        var trailing = AgentOptions.Parse(["--data-dir", path + Path.DirectorySeparatorChar]);

        Assert.Equal(plain.DataDirectory, trailing.DataDirectory);
    }

    [Fact]
    public void DefaultDataDirectoryUsesDeskMeshAndPreservesLegacyInstallations()
    {
        var root = Path.Combine(Path.GetTempPath(), $"DeskMesh-options-{Guid.NewGuid():N}");
        var legacy = Path.Combine(root, "LanSwitch");
        try
        {
            Assert.Equal(Path.Combine(root, "DeskMesh"), AgentOptions.ResolveDefaultDataDirectory(root));

            Directory.CreateDirectory(legacy);
            Assert.Equal(legacy, AgentOptions.ResolveDefaultDataDirectory(root));
        }
        finally
        {
            if (Directory.Exists(legacy)) Directory.Delete(legacy);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Fact]
    public void StartupRegistrationRequiresTheCurrentExecutablePath()
    {
        const string executable = @"C:\Apps\DeskMesh.exe";

        Assert.True(StartupRegistration.PointsToCurrentExecutable(@"""C:\Apps\DeskMesh.exe""", executable));
        Assert.False(StartupRegistration.PointsToCurrentExecutable(@"""C:\Apps\LanSwitch.exe""", executable));
        Assert.False(StartupRegistration.PointsToCurrentExecutable(@"""C:\Apps\DeskMesh.exe"" --headless", executable));
        Assert.False(StartupRegistration.PointsToCurrentExecutable("bad\0path", executable));
    }

    [Fact]
    public void PublicVersionPreservesTheSemanticPrereleaseLabel()
    {
        Assert.Equal("0.1.0-alpha.3", AppState.GetProductVersion(typeof(AppState).Assembly));
    }

    [Fact]
    public void DiscoveryBeaconValidationRejectsOversizedAndMalformedFields()
    {
        var valid = Beacon();

        Assert.True(PeerDiscoveryService.IsValidBeacon(valid));
        Assert.False(PeerDiscoveryService.IsValidBeacon(valid with { DeviceId = "not-a-device-id" }));
        Assert.False(PeerDiscoveryService.IsValidBeacon(valid with { Name = new string('x', 129) }));
        Assert.False(PeerDiscoveryService.IsValidBeacon(valid with { Port = 80 }));
        Assert.False(PeerDiscoveryService.IsValidBeacon(valid with { Fingerprint = new string('Z', 64) }));
        Assert.False(PeerDiscoveryService.IsValidBeacon(valid with { Capabilities = Enumerable.Repeat("input", 9).ToArray() }));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.8", true)]
    [InlineData("172.16.4.2", true)]
    [InlineData("8.8.8.8", false)]
    public void DiscoveryOnlyAcceptsPrivateAddresses(string value, bool expected)
    {
        Assert.Equal(expected, PeerDiscoveryService.IsPrivate(IPAddress.Parse(value)));
    }

    [Fact]
    public void RefreshingPairingCodeImmediatelyInvalidatesThePreviousCode()
    {
        var pairing = new PairingService(null!, null!, null!, null!, null!, AgentOptions.Parse([]));
        var previous = pairing.CurrentCode;

        var refreshed = pairing.RefreshCode();

        Assert.NotEqual(previous, refreshed.PairingCode);
        Assert.False(pairing.ValidateCode(previous));
        Assert.True(pairing.ValidateCode(refreshed.PairingCode));
        Assert.True(refreshed.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(4));
    }

    [Fact]
    public void RemotePairingProblemReturnsOnlyItsUsefulDetail()
    {
        const string problem = """
            {"title":"Bad Request","status":400,"detail":"配对码无效或已过期。","traceId":"trace"}
            """;

        Assert.Equal("配对码无效或已过期。", PairingService.RemoteErrorMessage(problem));
    }

    [Fact]
    public void UnpairedDiscoveryDirectoryHasABoundedCapacity()
    {
        var options = AgentOptions.Parse(["--data-dir", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))]);
        var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
        var directory = new PeerDirectory(new SettingsStore(options, identity));

        for (var index = 0; index < AgentOptions.MaxDiscoveredPeers; index++)
            Assert.NotNull(directory.Observe(Beacon(index), $"192.168.1.{index % 250 + 1}"));

        Assert.Null(directory.Observe(Beacon(AgentOptions.MaxDiscoveredPeers), "192.168.1.250"));
        Assert.Equal(AgentOptions.MaxDiscoveredPeers, directory.Snapshot().Count);
    }

    [Fact]
    public void UnpairedDiscoveryExpiresWithoutWaitingForAnotherBeacon()
    {
        var options = AgentOptions.Parse(["--data-dir", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))]);
        var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
        var directory = new PeerDirectory(new SettingsStore(options, identity));
        Assert.NotNull(directory.Observe(Beacon(), "192.168.1.20"));

        var removed = directory.PruneExpiredDiscoveries(
            DateTimeOffset.UtcNow + AgentOptions.DiscoveredPeerTtl + TimeSpan.FromSeconds(1));

        Assert.Equal(1, removed);
        Assert.Empty(directory.Snapshot());
    }

    [Fact]
    public async Task RemovingPeerCancelsItsTrustSessionAndPersistsRevocation()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"LanSwitch-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var options = AgentOptions.Parse(["--data-dir", dataDirectory]);
            var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
            var settings = new SettingsStore(options, identity);
            var directory = new PeerDirectory(settings);
            var peerId = Guid.NewGuid().ToString("N");
            var fingerprint = new string('A', 64);
            await directory.StorePairAsync(new StoredPeer(peerId, "测试设备", "192.168.1.20", 45832,
                fingerprint, "certificate", DateTimeOffset.UtcNow));
            Assert.True(directory.TryGetTrustToken(peerId, fingerprint, out var trustToken));

            Assert.True(await directory.RemoveAsync(peerId, _ => { }));

            Assert.True(trustToken.IsCancellationRequested);
            Assert.False(directory.TryGetTrustToken(peerId, fingerprint, out _));
            Assert.Contains(fingerprint, settings.Snapshot.RevokedFingerprints ?? []);
        }
        finally
        {
            var settingsPath = Path.Combine(dataDirectory, "settings.json");
            var temporaryPath = settingsPath + ".new";
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            Directory.Delete(dataDirectory, recursive: false);
        }
    }

    private static DiscoveryBeacon Beacon(int seed = 0) => new(
        "_lanswitch._tcp.local",
        1,
        GuidUtility(seed),
        $"设备-{seed}",
        45832,
        new string('A', 64),
        ["clipboard", "files", "input", "display"]);

    private static string GuidUtility(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        return new Guid(bytes).ToString("N");
    }
}
