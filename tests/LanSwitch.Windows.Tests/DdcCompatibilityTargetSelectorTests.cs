using LanSwitch.Windows.Display;

namespace LanSwitch.Windows.Tests;

public sealed class DdcCompatibilityTargetSelectorTests
{
    [Fact]
    public void ACompleteSinglePhysicalMonitorIsReturned()
    {
        var monitor = Identity(@"\\.\DISPLAY1", 0, "monitor-a", "Example monitor");

        var selected = DdcCompatibilityTargetSelector.Select([monitor]);

        Assert.Equal(monitor, Assert.Single(selected));
    }

    [Fact]
    public void MultiplePhysicalMonitorsBehindOneLogicalDisplayAreRejectedAsAmbiguous()
    {
        var first = Identity(@"\\.\DISPLAY1", 0, "monitor-a", "First monitor");
        var second = Identity(@"\\.\DISPLAY1", 1, "monitor-b", "Second monitor");

        var selected = DdcCompatibilityTargetSelector.Select([first, second]);

        Assert.Empty(selected);
    }

    [Fact]
    public void DuplicateEnumerationOfOnePhysicalMonitorFailsClosed()
    {
        var monitor = Identity(@"\\.\DISPLAY1", 0, "monitor-a", "Example monitor");

        var selected = DdcCompatibilityTargetSelector.Select([monitor, monitor]);

        Assert.Empty(selected);
    }

    [Theory]
    [InlineData("missing-description")]
    [InlineData("missing-fingerprint")]
    [InlineData("forged-monitor-id")]
    [InlineData("missing-display-position")]
    public void IncompleteOrCallerForgedIdentitiesAreNotReturned(string defect)
    {
        var monitor = Identity(@"\\.\DISPLAY1", 0, "monitor-a", "Example monitor");
        monitor = defect switch
        {
            "missing-description" => monitor with { Description = string.Empty },
            "missing-fingerprint" => monitor with { TopologyFingerprint = string.Empty },
            "forged-monitor-id" => monitor with { MonitorId = "caller-selected" },
            _ => monitor with { DisplayDeviceName = string.Empty }
        };

        var selected = DdcCompatibilityTargetSelector.Select([monitor]);

        Assert.Empty(selected);
    }

    [Fact]
    public void TargetsOnDifferentLogicalDisplaysAreReturnedInStableOrder()
    {
        var second = Identity(@"\\.\DISPLAY2", 0, "monitor-b", "Second monitor");
        var first = Identity(@"\\.\DISPLAY1", 0, "monitor-a", "First monitor");

        var selected = DdcCompatibilityTargetSelector.Select([second, first]);

        Assert.Equal([first, second], selected);
    }

    private static DdcMonitorIdentity Identity(
        string displayDeviceName,
        uint physicalIndex,
        string deviceInstance,
        string description)
    {
        var fingerprint = DdcMonitorIdentityRules.CreateTopologyFingerprint(
            "monitor-device-interface",
            deviceInstance);
        return new DdcMonitorIdentity(
            DdcMonitorIdentityRules.CreateMonitorId(
                displayDeviceName,
                physicalIndex,
                fingerprint),
            displayDeviceName,
            physicalIndex,
            description,
            fingerprint);
    }
}
