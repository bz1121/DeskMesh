namespace LanSwitch.Windows.Display;

public interface IDdcMonitorController : IDisposable
{
    Task<IReadOnlyList<DdcMonitorIdentity>> EnumerateCompatibilityTargetsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DdcMonitorProbeResult>> ProbeAsync(
        DdcProbeOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<DdcReadResult> ReadInputSourceAsync(
        string monitorId,
        CancellationToken cancellationToken = default);

    Task<DdcWriteResult> WriteInputSourceAsync(
        string monitorId,
        uint inputSource,
        DdcProbeOptions? verificationOptions = null,
        CancellationToken cancellationToken = default);

    Task<DdcWriteResult> WriteInputSourceCompatibilityAsync(
        DdcMonitorIdentity expectedMonitor,
        uint inputSource,
        DdcWriteOnlyCompatibilityOptions? options = null,
        CancellationToken cancellationToken = default);
}
