using LanSwitch.Windows.Display;

namespace LanSwitch.Windows.Tests;

public sealed class DdcProbeAuthorizationCacheTests
{
    [Fact]
    public void BeginningAProbeImmediatelyRevokesPreviousAuthorizations()
    {
        var cache = new DdcProbeAuthorizationCache();
        var previous = Identity("old", "FINGERPRINT-A");
        cache.Commit([previous]);

        cache.BeginProbe();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(previous.MonitorId, out _));
    }

    [Fact]
    public void ReadyMonitorsAreInvisibleUntilTheProbeCommits()
    {
        var cache = new DdcProbeAuthorizationCache();
        var candidate = Identity("new", "FINGERPRINT-B");
        cache.BeginProbe();

        Assert.False(cache.TryGet(candidate.MonitorId, out _));

        cache.Commit([candidate]);
        Assert.True(cache.TryGet(candidate.MonitorId, out var authorized));
        Assert.Equal(candidate, authorized);
    }

    [Fact]
    public void AReplacementAtTheSameDisplayLocationDoesNotMatch()
    {
        var expected = Identity("monitor", "FINGERPRINT-A");
        var replacement = expected with { TopologyFingerprint = "FINGERPRINT-B" };

        Assert.True(DdcMonitorIdentityRules.IsSameLocation(expected, replacement));
        Assert.True(DdcMonitorIdentityRules.IsSamePhysicalMonitor(expected, expected));
        Assert.False(DdcMonitorIdentityRules.IsSamePhysicalMonitor(expected, replacement));
    }

    [Fact]
    public void AnIdentityWithoutADeviceInterfaceFingerprintCannotAuthorizeDdc()
    {
        var expected = Identity("monitor", string.Empty);
        var cache = new DdcProbeAuthorizationCache();
        cache.Commit([expected]);

        Assert.False(DdcMonitorIdentityRules.HasStableTopologyIdentity(expected));
        Assert.False(DdcMonitorIdentityRules.IsSamePhysicalMonitor(expected, expected));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void TopologyFingerprintIsNormalizedAndSensitiveToDeviceIdentity()
    {
        var first = DdcMonitorIdentityRules.CreateTopologyFingerprint(" monitor\\one ", "device-a");
        var same = DdcMonitorIdentityRules.CreateTopologyFingerprint("MONITOR\\ONE", "DEVICE-A");
        var replacement = DdcMonitorIdentityRules.CreateTopologyFingerprint("MONITOR\\ONE", "DEVICE-B");

        Assert.Equal(first, same);
        Assert.NotEqual(first, replacement);
        Assert.Equal(32, first.Length);
    }

    private static DdcMonitorIdentity Identity(string monitorId, string topologyFingerprint) => new(
        monitorId,
        @"\\.\DISPLAY1",
        0,
        "Example monitor",
        topologyFingerprint);
}
