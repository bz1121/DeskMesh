using System.Security.Cryptography;
using System.Text;
using LanSwitch.Windows.Interop;

namespace LanSwitch.Windows.Display;

internal sealed class DdcProbeAuthorizationCache
{
    private readonly Dictionary<string, DdcMonitorIdentity> _authorized =
        new(StringComparer.OrdinalIgnoreCase);

    internal int Count => _authorized.Count;

    internal void BeginProbe() => _authorized.Clear();

    internal void Commit(IEnumerable<DdcMonitorIdentity> readyMonitors)
    {
        ArgumentNullException.ThrowIfNull(readyMonitors);
        var replacement = new Dictionary<string, DdcMonitorIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in readyMonitors)
        {
            if (!DdcMonitorIdentityRules.HasStableTopologyIdentity(monitor))
            {
                continue;
            }

            replacement[monitor.MonitorId] = monitor;
        }

        _authorized.Clear();
        foreach (var item in replacement)
        {
            _authorized.Add(item.Key, item.Value);
        }
    }

    internal bool TryGet(string monitorId, out DdcMonitorIdentity identity) =>
        _authorized.TryGetValue(monitorId, out identity!);

    internal bool Revoke(string monitorId) => _authorized.Remove(monitorId);
}

internal static class DdcMonitorIdentityRules
{
    internal static bool HasStableTopologyIdentity(DdcMonitorIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.TopologyFingerprint);

    internal static bool IsStrictExplicitTarget(DdcMonitorIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.MonitorId) &&
        !string.IsNullOrWhiteSpace(identity.DisplayDeviceName) &&
        !string.IsNullOrWhiteSpace(identity.Description) &&
        !string.IsNullOrWhiteSpace(identity.TopologyFingerprint) &&
        identity.TopologyFingerprint.Length == 32 &&
        identity.TopologyFingerprint.All(Uri.IsHexDigit) &&
        string.Equals(
            identity.MonitorId,
            CreateMonitorId(
                identity.DisplayDeviceName,
                identity.PhysicalMonitorIndex,
                identity.TopologyFingerprint),
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsSameLocation(DdcMonitorIdentity expected, DdcMonitorIdentity current) =>
        expected.PhysicalMonitorIndex == current.PhysicalMonitorIndex &&
        string.Equals(
            expected.DisplayDeviceName,
            current.DisplayDeviceName,
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsSamePhysicalMonitor(DdcMonitorIdentity expected, DdcMonitorIdentity current) =>
        IsSameLocation(expected, current) &&
        HasStableTopologyIdentity(expected) &&
        HasStableTopologyIdentity(current) &&
        string.Equals(expected.MonitorId, current.MonitorId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            expected.TopologyFingerprint,
            current.TopologyFingerprint,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.Description, current.Description, StringComparison.Ordinal);

    internal static string CreateTopologyFingerprint(params string?[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var normalized = string.Join(
            '\n',
            components.Select(static component => (component ?? string.Empty).Trim().ToUpperInvariant()));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest.AsSpan(0, 16));
    }

    internal static string CreateMonitorId(
        string displayDeviceName,
        uint physicalIndex,
        string topologyFingerprint) =>
        $"{displayDeviceName}|{physicalIndex}|{topologyFingerprint}";
}

internal readonly record struct DdcWriteOnlyCompatibilityDecision(
    bool IsAllowed,
    DdcWriteStatus RejectionStatus,
    string? ErrorMessage);

internal static class DdcWriteOnlyCompatibilityPolicy
{
    private static readonly HashSet<uint> BuiltInAllowedInputSources =
    [
        DdcKnownInputSources.DisplayPort1,
        DdcKnownInputSources.Hdmi1
    ];

    internal static DdcWriteOnlyCompatibilityDecision Evaluate(
        DdcMonitorIdentity expectedMonitor,
        uint inputSource,
        DdcWriteOnlyCompatibilityOptions? options)
    {
        ArgumentNullException.ThrowIfNull(expectedMonitor);
        var effectiveOptions = options ?? new DdcWriteOnlyCompatibilityOptions();
        if (!effectiveOptions.AcknowledgeUnverifiedWriteRisk)
        {
            return new DdcWriteOnlyCompatibilityDecision(
                false,
                DdcWriteStatus.CompatibilityModeDisabled,
                "Write-only DDC compatibility mode requires explicit risk acknowledgement.");
        }

        if (!IsAllowedInputSource(inputSource))
        {
            return new DdcWriteOnlyCompatibilityDecision(
                false,
                DdcWriteStatus.InputSourceNotAllowed,
                "Write-only compatibility mode accepts only DP1 (0x0F) or HDMI1 (0x11).");
        }

        if (!DdcMonitorIdentityRules.IsStrictExplicitTarget(expectedMonitor))
        {
            return new DdcWriteOnlyCompatibilityDecision(
                false,
                DdcWriteStatus.Unsupported,
                "Write-only compatibility mode requires a complete monitor identity returned by this controller.");
        }

        return new DdcWriteOnlyCompatibilityDecision(true, default, null);
    }

    internal static bool IsAllowedInputSource(uint inputSource) =>
        BuiltInAllowedInputSources.Contains(inputSource);

    internal static DdcWriteStatus ClassifyNativeSetFailure(int errorCode) => errorCode is
        0 or
        NativeMethods.ErrorInvalidFunction or
        NativeMethods.ErrorGenFailure or
        NativeMethods.ErrorNotSupported
            ? DdcWriteStatus.Unsupported
            : DdcWriteStatus.Rejected;
}

internal static class DdcCompatibilityTargetSelector
{
    internal static IReadOnlyList<DdcMonitorIdentity> Select(
        IEnumerable<DdcMonitorIdentity> enumeratedMonitors)
    {
        ArgumentNullException.ThrowIfNull(enumeratedMonitors);
        var snapshot = enumeratedMonitors.ToArray();

        // The DXVA2 API does not define an ordering relationship for multiple physical
        // monitors behind one logical display. Only a logical display with exactly one
        // physical identity is an unambiguous write-only target.
        var uniquePhysicalTargets = snapshot
            .GroupBy(
                static monitor => monitor.DisplayDeviceName ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.Single())
            .Where(DdcMonitorIdentityRules.IsStrictExplicitTarget)
            .ToArray();

        // Fail closed if a driver exposes the same derived monitor ID more than once.
        return uniquePhysicalTargets
            .GroupBy(static monitor => monitor.MonitorId, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.Single())
            .OrderBy(static monitor => monitor.DisplayDeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static monitor => monitor.PhysicalMonitorIndex)
            .ToArray();
    }
}

internal static class DdcPostCommandVerification
{
    internal static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (delay == TimeSpan.Zero)
        {
            return true;
        }

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
