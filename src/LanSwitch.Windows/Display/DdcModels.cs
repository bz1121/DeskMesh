namespace LanSwitch.Windows.Display;

public enum DdcVcpCodeType
{
    Momentary,
    SetParameter
}

public readonly record struct DdcVcpSample(
    uint CurrentValue,
    uint MaximumValue,
    DdcVcpCodeType CodeType);

public sealed record DdcMonitorIdentity(
    string MonitorId,
    string DisplayDeviceName,
    uint PhysicalMonitorIndex,
    string Description,
    string TopologyFingerprint = "");

public enum DdcProbeStatus
{
    Ready,
    Unsupported,
    Unstable,
    Error
}

public sealed record DdcMonitorProbeResult(
    DdcMonitorIdentity Monitor,
    DdcProbeStatus Status,
    DdcVcpSample? FirstSample,
    DdcVcpSample? SecondSample,
    int ErrorCode,
    string? ErrorMessage)
{
    public bool IsReady => Status == DdcProbeStatus.Ready;
}

public sealed record DdcProbeOptions
{
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public void Validate()
    {
        if (SampleInterval < TimeSpan.Zero || SampleInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(SampleInterval),
                "The DDC sample interval must be between zero and five seconds.");
        }
    }
}

public static class DdcKnownInputSources
{
    public const uint DisplayPort1 = 0x0F;
    public const uint Hdmi1 = 0x11;
}

public sealed record DdcWriteOnlyCompatibilityOptions
{
    // This mode deliberately writes VCP 0x60 without first proving that it is readable.
    // Requiring an affirmative value on every call prevents it from becoming a fallback
    // that silently weakens the normal Probe -> Write gate.
    public bool AcknowledgeUnverifiedWriteRisk { get; init; }
}

public enum DdcReadStatus
{
    Succeeded,
    NotProbed,
    MonitorUnavailable,
    Failed,
    IdentityChanged
}

public sealed record DdcReadResult(
    DdcReadStatus Status,
    DdcMonitorIdentity? Monitor,
    DdcVcpSample? Sample,
    int ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Status == DdcReadStatus.Succeeded;
}

public enum DdcWriteStatus
{
    Verified,
    AppliedButUnverified,
    AppliedWithUnexpectedValue,
    AppliedWithUnstableSamples,
    NotProbed,
    MonitorUnavailable,
    Rejected,
    IdentityChanged,
    Unsupported,
    CompatibilityModeDisabled,
    InputSourceNotAllowed,
    CommandIssuedUnverified
}

public sealed record DdcWriteResult(
    DdcWriteStatus Status,
    DdcMonitorIdentity? Monitor,
    uint RequestedValue,
    DdcVcpSample? FirstVerificationSample,
    DdcVcpSample? SecondVerificationSample,
    int ErrorCode,
    string? ErrorMessage)
{
    public bool CommandWasIssued => Status is
        DdcWriteStatus.Verified or
        DdcWriteStatus.AppliedButUnverified or
        DdcWriteStatus.AppliedWithUnexpectedValue or
        DdcWriteStatus.AppliedWithUnstableSamples or
        DdcWriteStatus.CommandIssuedUnverified;
}

public static class DdcSampleStability
{
    public static bool IsStable(DdcVcpSample first, DdcVcpSample second) => first == second;
}
