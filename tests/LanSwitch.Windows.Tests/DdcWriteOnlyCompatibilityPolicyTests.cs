using LanSwitch.Windows.Display;

namespace LanSwitch.Windows.Tests;

public sealed class DdcWriteOnlyCompatibilityPolicyTests
{
    [Fact]
    public void CompatibilityModeIsDisabledByDefault()
    {
        var decision = DdcWriteOnlyCompatibilityPolicy.Evaluate(
            Identity(),
            DdcKnownInputSources.Hdmi1,
            options: null);

        Assert.False(decision.IsAllowed);
        Assert.Equal(DdcWriteStatus.CompatibilityModeDisabled, decision.RejectionStatus);
    }

    [Theory]
    [InlineData(0x0F)]
    [InlineData(0x11)]
    public void ExplicitOptInAllowsOnlyDp1AndHdmi1(uint inputSource)
    {
        var decision = DdcWriteOnlyCompatibilityPolicy.Evaluate(
            Identity(),
            inputSource,
            EnabledOptions());

        Assert.True(decision.IsAllowed);
    }

    [Theory]
    [InlineData(0x12)]
    [InlineData(0x1B)]
    public void EveryOtherInputSourceIsRejectedBeforeNativeEnumeration(uint inputSource)
    {
        var decision = DdcWriteOnlyCompatibilityPolicy.Evaluate(
            Identity(),
            inputSource,
            EnabledOptions());

        Assert.False(decision.IsAllowed);
        Assert.Equal(DdcWriteStatus.InputSourceNotAllowed, decision.RejectionStatus);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x100)]
    public void ValuesOutsideTheCandidateRangeRemainRejected(uint inputSource)
    {
        var decision = DdcWriteOnlyCompatibilityPolicy.Evaluate(
            Identity(),
            inputSource,
            EnabledOptions());

        Assert.False(decision.IsAllowed);
        Assert.Equal(DdcWriteStatus.InputSourceNotAllowed, decision.RejectionStatus);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ACompleteControllerIssuedIdentityIsRequired(bool keepDescription, bool keepFingerprint)
    {
        var identity = Identity() with
        {
            Description = keepDescription ? "Example monitor" : string.Empty,
            TopologyFingerprint = keepFingerprint
                ? Identity().TopologyFingerprint
                : string.Empty
        };

        var decision = DdcWriteOnlyCompatibilityPolicy.Evaluate(
            identity,
            DdcKnownInputSources.DisplayPort1,
            EnabledOptions());

        Assert.False(decision.IsAllowed);
        Assert.Equal(DdcWriteStatus.Unsupported, decision.RejectionStatus);
    }

    [Fact]
    public void ACallerCannotForgeAMonitorIdForAnOtherwiseCompleteIdentity()
    {
        var identity = Identity() with { MonitorId = "caller-selected" };

        var decision = DdcWriteOnlyCompatibilityPolicy.Evaluate(
            identity,
            DdcKnownInputSources.Hdmi1,
            EnabledOptions());

        Assert.False(decision.IsAllowed);
        Assert.Equal(DdcWriteStatus.Unsupported, decision.RejectionStatus);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(50)]
    public void NativeFailuresThatCannotProveWriteSupportAreReportedAsUnsupported(int errorCode)
    {
        Assert.Equal(
            DdcWriteStatus.Unsupported,
            DdcWriteOnlyCompatibilityPolicy.ClassifyNativeSetFailure(errorCode));
    }

    [Fact]
    public void NativePolicyOrPermissionFailuresRemainRejected()
    {
        Assert.Equal(
            DdcWriteStatus.Rejected,
            DdcWriteOnlyCompatibilityPolicy.ClassifyNativeSetFailure(5));
    }

    [Fact]
    public void CommandIssuedUnverifiedMeansOnlyThatTheNativeCommandWasAccepted()
    {
        var result = new DdcWriteResult(
            DdcWriteStatus.CommandIssuedUnverified,
            Identity(),
            DdcKnownInputSources.Hdmi1,
            null,
            null,
            0,
            "Command issued without readback.");

        Assert.True(result.CommandWasIssued);
        Assert.NotEqual(DdcWriteStatus.AppliedButUnverified, result.Status);
    }

    private static DdcWriteOnlyCompatibilityOptions EnabledOptions() => new()
    {
        AcknowledgeUnverifiedWriteRisk = true
    };

    private static DdcMonitorIdentity Identity()
    {
        const string displayDeviceName = @"\\.\DISPLAY1";
        var fingerprint = DdcMonitorIdentityRules.CreateTopologyFingerprint(
            "monitor-device-interface",
            "device-instance");
        return new DdcMonitorIdentity(
            DdcMonitorIdentityRules.CreateMonitorId(displayDeviceName, 0, fingerprint),
            displayDeviceName,
            0,
            "Example monitor",
            fingerprint);
    }
}
