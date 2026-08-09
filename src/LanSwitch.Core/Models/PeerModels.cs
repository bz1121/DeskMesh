namespace LanSwitch.Core.Models;

public enum PeerCapability
{
    ClipboardText,
    ClipboardImage,
    FileTransfer,
    InputCapture,
    InputInjection,
    DisplaySwitch
}

public sealed record PeerSummary
{
    public required string DeviceId { get; init; }

    public required string DisplayName { get; init; }

    public required string Address { get; init; }

    public bool IsOnline { get; init; }

    public int? LatencyMs { get; init; }

    public IReadOnlyList<PeerCapability> Capabilities { get; init; } = Array.Empty<PeerCapability>();

    public string? CertificateFingerprint { get; init; }

    public DateTimeOffset LastSeenUtc { get; init; }
}

public enum FocusState
{
    Local,
    PreparingRemote,
    Remote,
    Recovering
}

public sealed record FocusSnapshot(
    FocusState State,
    string LocalDeviceId,
    string? TargetDeviceId,
    long Epoch,
    bool TargetReady,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastHeartbeatUtc);

public sealed record DisplayMapping
{
    public required string MonitorId { get; init; }

    public required string MonitorName { get; init; }

    public required string PeerDeviceId { get; init; }

    public uint LocalInputValue { get; init; }

    public uint PeerInputValue { get; init; }

    public bool Enabled { get; init; }

    public DateTimeOffset? LastVerifiedUtc { get; init; }
}
