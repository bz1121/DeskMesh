using System.ComponentModel;
using System.Runtime.InteropServices;
using LanSwitch.Windows.Interop;

namespace LanSwitch.Windows.Display;

public sealed class WindowsDdcMonitorController : IDdcMonitorController
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly DdcProbeAuthorizationCache _probeAuthorizations = new();
    private int _disposed;

    public async Task<IReadOnlyList<DdcMonitorIdentity>> EnumerateCompatibilityTargetsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            using var monitors = EnumeratePhysicalMonitors();
            cancellationToken.ThrowIfCancellationRequested();
            return DdcCompatibilityTargetSelector.Select(
                monitors.Items.Select(static monitor => monitor.Identity));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<DdcMonitorProbeResult>> ProbeAsync(
        DdcProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var effectiveOptions = options ?? new DdcProbeOptions();
        effectiveOptions.Validate();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            // A new probe attempt always disarms the previous topology first. Results are
            // committed only after the complete probe finishes without cancellation.
            _probeAuthorizations.BeginProbe();
            using var monitors = EnumeratePhysicalMonitors();
            var results = new List<DdcMonitorProbeResult>(monitors.Items.Count);
            var readyMonitors = new List<DdcMonitorIdentity>(monitors.Items.Count);

            foreach (var monitor in monitors.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DdcMonitorIdentityRules.HasStableTopologyIdentity(monitor.Identity))
                {
                    results.Add(new DdcMonitorProbeResult(
                        monitor.Identity,
                        DdcProbeStatus.Error,
                        null,
                        null,
                        0,
                        "Windows did not expose a stable monitor device-interface identity; " +
                        "DDC writes remain disabled for this monitor."));
                    continue;
                }

                var first = TryReadSample(monitor.Handle);
                if (!first.Succeeded)
                {
                    results.Add(new DdcMonitorProbeResult(
                        monitor.Identity,
                        DdcProbeStatus.Unsupported,
                        null,
                        null,
                        first.ErrorCode,
                        first.ErrorMessage));
                    continue;
                }

                await DelayIfNeeded(effectiveOptions.SampleInterval, cancellationToken).ConfigureAwait(false);
                var second = TryReadSample(monitor.Handle);
                if (!second.Succeeded)
                {
                    results.Add(new DdcMonitorProbeResult(
                        monitor.Identity,
                        DdcProbeStatus.Error,
                        first.Sample,
                        null,
                        second.ErrorCode,
                        second.ErrorMessage));
                    continue;
                }

                var stable = DdcSampleStability.IsStable(first.Sample!.Value, second.Sample!.Value);
                var status = stable ? DdcProbeStatus.Ready : DdcProbeStatus.Unstable;
                if (stable)
                {
                    readyMonitors.Add(monitor.Identity);
                }

                results.Add(new DdcMonitorProbeResult(
                    monitor.Identity,
                    status,
                    first.Sample,
                    second.Sample,
                    0,
                    stable ? null : "The two VCP 0x60 samples did not match."));
            }

            cancellationToken.ThrowIfCancellationRequested();
            _probeAuthorizations.Commit(readyMonitors);
            return results;
        }
        catch (Win32Exception exception)
        {
            _probeAuthorizations.BeginProbe();
            return [
                new DdcMonitorProbeResult(
                    new DdcMonitorIdentity("enumeration", string.Empty, 0, string.Empty),
                    DdcProbeStatus.Error,
                    null,
                    null,
                    exception.NativeErrorCode,
                    exception.Message)
            ];
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<DdcReadResult> ReadInputSourceAsync(
        string monitorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        ThrowIfDisposed();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_probeAuthorizations.TryGet(monitorId, out var knownMonitor))
            {
                return new DdcReadResult(
                    DdcReadStatus.NotProbed,
                    null,
                    null,
                    0,
                    "Probe the monitor successfully before reading VCP 0x60.");
            }

            using var monitors = EnumeratePhysicalMonitors();
            var monitor = FindMonitorAtLocation(monitors, knownMonitor);
            if (monitor is null)
            {
                _probeAuthorizations.Revoke(monitorId);
                return new DdcReadResult(
                    DdcReadStatus.MonitorUnavailable,
                    knownMonitor,
                    null,
                    0,
                    "The probed monitor is no longer available.");
            }

            if (!DdcMonitorIdentityRules.IsSamePhysicalMonitor(knownMonitor, monitor.Identity))
            {
                _probeAuthorizations.Revoke(monitorId);
                return new DdcReadResult(
                    DdcReadStatus.IdentityChanged,
                    monitor.Identity,
                    null,
                    0,
                    "The monitor at this display location changed after probing; probe it again before reading.");
            }

            var read = TryReadSample(monitor.Handle);
            return read.Succeeded
                ? new DdcReadResult(DdcReadStatus.Succeeded, monitor.Identity, read.Sample, 0, null)
                : new DdcReadResult(
                    DdcReadStatus.Failed,
                    monitor.Identity,
                    null,
                    read.ErrorCode,
                    read.ErrorMessage);
        }
        catch (Win32Exception exception)
        {
            _probeAuthorizations.Revoke(monitorId);
            return new DdcReadResult(
                DdcReadStatus.Failed,
                null,
                null,
                exception.NativeErrorCode,
                exception.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<DdcWriteResult> WriteInputSourceAsync(
        string monitorId,
        uint inputSource,
        DdcProbeOptions? verificationOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        ThrowIfDisposed();
        var effectiveOptions = verificationOptions ?? new DdcProbeOptions();
        effectiveOptions.Validate();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_probeAuthorizations.TryGet(monitorId, out var knownMonitor))
            {
                return new DdcWriteResult(
                    DdcWriteStatus.NotProbed,
                    null,
                    inputSource,
                    null,
                    null,
                    0,
                    "Probe the monitor successfully before writing VCP 0x60.");
            }

            using var monitors = EnumeratePhysicalMonitors();
            var monitor = FindMonitorAtLocation(monitors, knownMonitor);
            if (monitor is null)
            {
                _probeAuthorizations.Revoke(monitorId);
                return new DdcWriteResult(
                    DdcWriteStatus.MonitorUnavailable,
                    knownMonitor,
                    inputSource,
                    null,
                    null,
                    0,
                    "The probed monitor is no longer available.");
            }

            if (!DdcMonitorIdentityRules.IsSamePhysicalMonitor(knownMonitor, monitor.Identity))
            {
                _probeAuthorizations.Revoke(monitorId);
                return new DdcWriteResult(
                    DdcWriteStatus.IdentityChanged,
                    monitor.Identity,
                    inputSource,
                    null,
                    null,
                    0,
                    "The monitor at this display location changed after probing; probe it again before writing.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.SetVCPFeature(
                    monitor.Handle,
                    NativeMethods.InputSourceVcpCode,
                    inputSource))
            {
                var error = Marshal.GetLastWin32Error();
                return new DdcWriteResult(
                    DdcWriteStatus.Rejected,
                    monitor.Identity,
                    inputSource,
                    null,
                    null,
                    error,
                    FormatNativeError("SetVCPFeature(0x60)", error));
            }

            if (!await DdcPostCommandVerification.WaitAsync(
                    effectiveOptions.SampleInterval,
                    cancellationToken).ConfigureAwait(false))
            {
                return AppliedButUnverifiedAfterCancellation(monitor.Identity, inputSource, null);
            }

            var first = TryReadSample(monitor.Handle);
            if (!first.Succeeded)
            {
                return AppliedButUnverified(monitor.Identity, inputSource, first);
            }

            if (!await DdcPostCommandVerification.WaitAsync(
                    effectiveOptions.SampleInterval,
                    cancellationToken).ConfigureAwait(false))
            {
                return AppliedButUnverifiedAfterCancellation(monitor.Identity, inputSource, first.Sample);
            }

            var second = TryReadSample(monitor.Handle);
            if (!second.Succeeded)
            {
                return new DdcWriteResult(
                    DdcWriteStatus.AppliedButUnverified,
                    monitor.Identity,
                    inputSource,
                    first.Sample,
                    null,
                    second.ErrorCode,
                    second.ErrorMessage);
            }

            if (!DdcSampleStability.IsStable(first.Sample!.Value, second.Sample!.Value))
            {
                return new DdcWriteResult(
                    DdcWriteStatus.AppliedWithUnstableSamples,
                    monitor.Identity,
                    inputSource,
                    first.Sample,
                    second.Sample,
                    0,
                    "The write was issued, but the two verification samples did not match.");
            }

            var status = second.Sample.Value.CurrentValue == inputSource
                ? DdcWriteStatus.Verified
                : DdcWriteStatus.AppliedWithUnexpectedValue;
            return new DdcWriteResult(
                status,
                monitor.Identity,
                inputSource,
                first.Sample,
                second.Sample,
                0,
                status == DdcWriteStatus.Verified
                    ? null
                    : "The monitor reported a different VCP 0x60 value after the write.");
        }
        catch (Win32Exception exception)
        {
            _probeAuthorizations.Revoke(monitorId);
            return new DdcWriteResult(
                DdcWriteStatus.Rejected,
                null,
                inputSource,
                null,
                null,
                exception.NativeErrorCode,
                exception.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<DdcWriteResult> WriteInputSourceCompatibilityAsync(
        DdcMonitorIdentity expectedMonitor,
        uint inputSource,
        DdcWriteOnlyCompatibilityOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedMonitor);
        ThrowIfDisposed();
        var decision = DdcWriteOnlyCompatibilityPolicy.Evaluate(
            expectedMonitor,
            inputSource,
            options);
        if (!decision.IsAllowed)
        {
            return new DdcWriteResult(
                decision.RejectionStatus,
                expectedMonitor,
                inputSource,
                null,
                null,
                0,
                decision.ErrorMessage);
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var monitors = EnumeratePhysicalMonitors();
            var monitor = FindMonitorAtLocation(monitors, expectedMonitor);
            if (monitor is null)
            {
                _probeAuthorizations.Revoke(expectedMonitor.MonitorId);
                return new DdcWriteResult(
                    DdcWriteStatus.MonitorUnavailable,
                    expectedMonitor,
                    inputSource,
                    null,
                    null,
                    0,
                    "The explicitly targeted monitor is no longer available.");
            }

            if (!DdcMonitorIdentityRules.IsStrictExplicitTarget(monitor.Identity))
            {
                _probeAuthorizations.Revoke(expectedMonitor.MonitorId);
                return new DdcWriteResult(
                    DdcWriteStatus.Unsupported,
                    monitor.Identity,
                    inputSource,
                    null,
                    null,
                    0,
                    "Windows no longer exposes a complete identity for the explicitly targeted monitor.");
            }

            if (!DdcMonitorIdentityRules.IsSamePhysicalMonitor(expectedMonitor, monitor.Identity))
            {
                _probeAuthorizations.Revoke(expectedMonitor.MonitorId);
                return new DdcWriteResult(
                    DdcWriteStatus.IdentityChanged,
                    monitor.Identity,
                    inputSource,
                    null,
                    null,
                    0,
                    "The monitor at the explicitly selected display location changed; select it again before writing.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.SetVCPFeature(
                    monitor.Handle,
                    NativeMethods.InputSourceVcpCode,
                    inputSource))
            {
                var error = Marshal.GetLastWin32Error();
                // The display can be unplugged in the narrow interval between identity
                // validation and SetVCPFeature. Re-enumerate before classifying the native
                // failure so that a hot-plug race is not reported as mere incompatibility.
                using var refreshedMonitors = EnumeratePhysicalMonitors();
                var refreshedMonitor = FindMonitorAtLocation(refreshedMonitors, expectedMonitor);
                if (refreshedMonitor is null)
                {
                    _probeAuthorizations.Revoke(expectedMonitor.MonitorId);
                    return new DdcWriteResult(
                        DdcWriteStatus.MonitorUnavailable,
                        expectedMonitor,
                        inputSource,
                        null,
                        null,
                        error,
                        "The explicitly targeted monitor disappeared while issuing the write-only command.");
                }

                if (!DdcMonitorIdentityRules.IsStrictExplicitTarget(refreshedMonitor.Identity))
                {
                    _probeAuthorizations.Revoke(expectedMonitor.MonitorId);
                    return new DdcWriteResult(
                        DdcWriteStatus.Unsupported,
                        refreshedMonitor.Identity,
                        inputSource,
                        null,
                        null,
                        error,
                        "Windows stopped exposing a complete identity while issuing the write-only command.");
                }

                if (!DdcMonitorIdentityRules.IsSamePhysicalMonitor(expectedMonitor, refreshedMonitor.Identity))
                {
                    _probeAuthorizations.Revoke(expectedMonitor.MonitorId);
                    return new DdcWriteResult(
                        DdcWriteStatus.IdentityChanged,
                        refreshedMonitor.Identity,
                        inputSource,
                        null,
                        null,
                        error,
                        "The explicitly targeted monitor changed while issuing the write-only command.");
                }

                var status = DdcWriteOnlyCompatibilityPolicy.ClassifyNativeSetFailure(error);
                return new DdcWriteResult(
                    status,
                    monitor.Identity,
                    inputSource,
                    null,
                    null,
                    error,
                    FormatNativeError("SetVCPFeature(0x60) in write-only compatibility mode", error));
            }

            var cancellationObserved = cancellationToken.IsCancellationRequested;
            return new DdcWriteResult(
                DdcWriteStatus.CommandIssuedUnverified,
                monitor.Identity,
                inputSource,
                null,
                null,
                cancellationObserved ? NativeMethods.ErrorOperationAborted : 0,
                cancellationObserved
                    ? "The write-only VCP 0x60 command was issued before caller cancellation; its result cannot be verified."
                    : "The write-only VCP 0x60 command was issued by explicit opt-in; this monitor cannot verify it.");
        }
        catch (Win32Exception exception)
        {
            _probeAuthorizations.Revoke(expectedMonitor.MonitorId);
            return new DdcWriteResult(
                DdcWriteStatus.Rejected,
                expectedMonitor,
                inputSource,
                null,
                null,
                exception.NativeErrorCode,
                exception.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        // SemaphoreSlim does not allocate an OS handle unless AvailableWaitHandle is used.
        // Keeping it alive lets an already-running operation leave its finally block safely.
        _ = Interlocked.Exchange(ref _disposed, 1);
    }

    private static PhysicalMonitorCollection EnumeratePhysicalMonitors()
    {
        var logicalMonitors = new List<nint>();
        NativeMethods.MonitorEnumProcedure callback = (monitor, _, _, _) =>
        {
            logicalMonitors.Add(monitor);
            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero))
        {
            throw CreateWin32Exception("EnumDisplayMonitors");
        }

        GC.KeepAlive(callback);
        var result = new PhysicalMonitorCollection();
        try
        {
            foreach (var logicalMonitor in logicalMonitors)
            {
                var monitorInfo = new NativeMethods.MonitorInfoEx
                {
                    Size = checked((uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>()),
                    DeviceName = string.Empty
                };
                if (!NativeMethods.GetMonitorInfo(logicalMonitor, ref monitorInfo))
                {
                    throw CreateWin32Exception("GetMonitorInfoW");
                }

                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(
                        logicalMonitor,
                        out var count))
                {
                    throw CreateWin32Exception("GetNumberOfPhysicalMonitorsFromHMONITOR");
                }

                if (count == 0)
                {
                    continue;
                }

                var physical = new NativeMethods.PhysicalMonitor[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, physical))
                {
                    foreach (var partial in physical)
                    {
                        if (partial.Handle != nint.Zero)
                        {
                            _ = NativeMethods.DestroyPhysicalMonitor(partial.Handle);
                        }
                    }

                    throw CreateWin32Exception("GetPhysicalMonitorsFromHMONITOR");
                }

                // EnumDisplayDevices exposes a per-monitor device-interface name, but the
                // platform does not define an ordering relationship with a multi-element
                // PHYSICAL_MONITOR array. Stay fail-closed for that uncommon topology.
                var topologyFingerprint = physical.Length == 1
                    ? GetTopologyFingerprint(monitorInfo.DeviceName)
                    : string.Empty;
                for (var index = 0; index < physical.Length; index++)
                {
                    var native = physical[index];
                    var physicalIndex = checked((uint)index);
                    var identity = new DdcMonitorIdentity(
                        DdcMonitorIdentityRules.CreateMonitorId(
                            monitorInfo.DeviceName,
                            physicalIndex,
                            topologyFingerprint),
                        monitorInfo.DeviceName,
                        physicalIndex,
                        native.Description ?? string.Empty,
                        topologyFingerprint);
                    result.Items.Add(new PhysicalMonitorLease(native.Handle, identity));
                }
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static PhysicalMonitorLease? FindMonitorAtLocation(
        PhysicalMonitorCollection monitors,
        DdcMonitorIdentity identity) => monitors.Items.FirstOrDefault(
            monitor => DdcMonitorIdentityRules.IsSameLocation(identity, monitor.Identity));

    private static NativeReadResult TryReadSample(nint handle)
    {
        if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                handle,
                NativeMethods.InputSourceVcpCode,
                out var codeType,
                out var current,
                out var maximum))
        {
            return new NativeReadResult(
                true,
                new DdcVcpSample(
                    current,
                    maximum,
                    codeType == NativeMethods.MonitorVcpCodeType.Momentary
                        ? DdcVcpCodeType.Momentary
                        : DdcVcpCodeType.SetParameter),
                0,
                null);
        }

        var error = Marshal.GetLastWin32Error();
        return new NativeReadResult(
            false,
            null,
            error,
            FormatNativeError("GetVCPFeatureAndVCPFeatureReply(0x60)", error));
    }

    private static DdcWriteResult AppliedButUnverified(
        DdcMonitorIdentity monitor,
        uint inputSource,
        NativeReadResult read) => new(
            DdcWriteStatus.AppliedButUnverified,
            monitor,
            inputSource,
            null,
            null,
            read.ErrorCode,
            "The input-source write was issued, but DDC/CI became unavailable for verification. " +
            read.ErrorMessage);

    private static DdcWriteResult AppliedButUnverifiedAfterCancellation(
        DdcMonitorIdentity monitor,
        uint inputSource,
        DdcVcpSample? firstSample) => new(
            DdcWriteStatus.AppliedButUnverified,
            monitor,
            inputSource,
            firstSample,
            null,
            NativeMethods.ErrorOperationAborted,
            "The input-source write was issued, but caller cancellation stopped verification.");

    private static Task DelayIfNeeded(TimeSpan delay, CancellationToken cancellationToken) =>
        delay == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);

    private static string GetTopologyFingerprint(string displayDeviceName)
    {
        var identities = new List<string>();
        for (uint index = 0; ; index++)
        {
            var displayDevice = new NativeMethods.DisplayDevice
            {
                Size = checked((uint)Marshal.SizeOf<NativeMethods.DisplayDevice>()),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceId = string.Empty,
                DeviceKey = string.Empty
            };
            if (!NativeMethods.EnumDisplayDevices(
                    displayDeviceName,
                    index,
                    ref displayDevice,
                    NativeMethods.EddGetDeviceInterfaceName))
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(displayDevice.DeviceId))
            {
                identities.Add(string.Join(
                    '\n',
                    displayDevice.DeviceName,
                    displayDevice.DeviceId,
                    displayDevice.DeviceKey));
            }
        }

        identities.Sort(StringComparer.OrdinalIgnoreCase);
        return DdcMonitorIdentityRules.CreateTopologyFingerprint([.. identities]);
    }

    private static Win32Exception CreateWin32Exception(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, FormatNativeError(operation, error));
    }

    private static string FormatNativeError(string operation, int error) =>
        error == 0
            ? $"{operation} failed; the monitor driver did not provide a Win32 error code."
            : $"{operation} failed with Win32 error {error}: {new Win32Exception(error).Message}";

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private readonly record struct NativeReadResult(
        bool Succeeded,
        DdcVcpSample? Sample,
        int ErrorCode,
        string? ErrorMessage);

    private sealed class PhysicalMonitorLease(nint handle, DdcMonitorIdentity identity) : IDisposable
    {
        private nint _handle = handle;

        internal nint Handle => _handle;

        internal DdcMonitorIdentity Identity { get; } = identity;

        public void Dispose()
        {
            var handleToDestroy = Interlocked.Exchange(ref _handle, nint.Zero);
            if (handleToDestroy != nint.Zero)
            {
                _ = NativeMethods.DestroyPhysicalMonitor(handleToDestroy);
            }
        }
    }

    private sealed class PhysicalMonitorCollection : IDisposable
    {
        internal List<PhysicalMonitorLease> Items { get; } = [];

        public void Dispose()
        {
            foreach (var item in Items)
            {
                item.Dispose();
            }

            Items.Clear();
        }
    }
}
