namespace LanSwitch.Agent.Infrastructure;

internal static class DisplayCompatibilitySettings
{
    internal static bool IsInputValueAllowed(uint value) => value is 0x0F or 0x11;

    internal static bool IsAutomaticReady(AgentSettings config, string? targetDeviceId = null)
    {
        if (!config.DdcWriteOnlyEnabled || string.IsNullOrWhiteSpace(config.DdcWriteOnlyMonitorId)) return false;
        var confirmed = new HashSet<uint>(config.DdcWriteOnlyConfirmedInputs ?? []);
        if (!confirmed.Contains(0x0F) || !confirmed.Contains(0x11)) return false;
        var mappings = config.DisplayMappings
            .Where(mapping => string.Equals(mapping.MonitorId, config.DdcWriteOnlyMonitorId,
                StringComparison.OrdinalIgnoreCase) && confirmed.Contains(mapping.VcpValue))
            .ToArray();
        var local = mappings.FirstOrDefault(mapping => mapping.DeviceId == config.DeviceId);
        if (local is null) return false;
        if (targetDeviceId is null || targetDeviceId == config.DeviceId)
            return mappings.Any(mapping => mapping.DeviceId != config.DeviceId && mapping.VcpValue != local.VcpValue);
        return mappings.Any(mapping => mapping.DeviceId == targetDeviceId && mapping.VcpValue != local.VcpValue);
    }
}
