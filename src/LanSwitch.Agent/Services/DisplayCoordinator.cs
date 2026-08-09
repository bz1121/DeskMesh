using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

public sealed class DisplayCoordinator(SettingsStore settings, AppState state)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _modeGate = new(1, 1);
    private readonly object _followPublisherGenerationGate = new();
    private CancellationTokenSource _followPublisherGeneration = new();
    private List<DisplayProbeView> _displays = [];
    private PendingCompatibilityTest? _pendingCompatibilityTest;
    public event Func<string, uint, CancellationToken, Task<DisplayOperationResult>>? SwitchRequested;
    public event Func<string, uint, CancellationToken, Task<DisplayOperationResult>>? CompatibilityWriteRequested;
    public event Func<CancellationToken, Task<IReadOnlyList<DisplayProbeView>>>? ProbeRequested;

    public IReadOnlyList<DisplayProbeView> Snapshot() { lock (_gate) return [.. _displays]; }

    public async Task<IReadOnlyList<DisplayProbeView>> ProbeAsync(CancellationToken cancellationToken)
    {
        using var modeLease = await EnterModeGateAsync(cancellationToken);
        return await ProbeUnderModeGateAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<DisplayProbeView>> ProbeUnderModeGateAsync(CancellationToken cancellationToken)
    {
        if (ProbeRequested is null)
        {
            state.SetDisplay(state.Display with { Message = "Windows DDC/CI 组件尚未就绪" });
            return Snapshot();
        }
        IReadOnlyList<DisplayProbeView> values = await ProbeRequested(cancellationToken);
        var config = settings.Snapshot;
        if (config.DdcWriteOnlyEnabled)
        {
            values = [.. values.Select(static display => display with
            {
                Stable = false,
                CurrentInput = null,
                CurrentLabel = null
            })];
        }
        lock (_gate) _displays = [.. values];
        var selected = config.DdcWriteOnlyEnabled
            ? values.FirstOrDefault(value => string.Equals(value.MonitorId, config.DdcWriteOnlyMonitorId,
                StringComparison.OrdinalIgnoreCase))
            : values.FirstOrDefault(static value => value.Supported && value.Stable)
            ?? values.FirstOrDefault(static value => value.Supported)
            ?? values.FirstOrDefault();
        var followEnabled = config.PhysicalFollowEnabled;
        var writeOnlyEnabled = config.DdcWriteOnlyEnabled;
        var writeOnlyMonitorId = config.DdcWriteOnlyMonitorId;
        IReadOnlyList<uint> writeOnlyConfirmedInputs = [.. (config.DdcWriteOnlyConfirmedInputs ?? [])];
        var writeOnlyAutomaticReady = DisplayCompatibilitySettings.IsAutomaticReady(config);
        var message = selected?.Message ?? "未发现支持 DDC/CI 的外接显示器";
        if (writeOnlyEnabled && (selected is null || !selected.WriteOnlyEligible))
        {
            await settings.UpdateAsync(current => current with
            {
                DdcWriteOnlyEnabled = false,
                DdcWriteOnlyMonitorId = null,
                DdcWriteOnlyConfirmedInputs = [],
                PhysicalFollowEnabled = false
            }, cancellationToken);
            writeOnlyEnabled = false;
            writeOnlyMonitorId = null;
            writeOnlyConfirmedInputs = [];
            writeOnlyAutomaticReady = false;
            followEnabled = false;
            lock (_gate) _pendingCompatibilityTest = null;
            message += "；显示器身份已变化，只写兼容模式已自动停用。";
        }
        if (followEnabled && (selected is null || !selected.Stable))
            message += "；本输入端暂时无法读取 DDC/CI，继续等待远端 Agent 的稳定确认。";
        state.SetDisplay(selected is null
            ? new DisplayView(false, false, null, null, followEnabled, writeOnlyEnabled,
                writeOnlyMonitorId, writeOnlyConfirmedInputs, writeOnlyAutomaticReady, message)
            : new DisplayView(selected.Supported, selected.Stable, selected.CurrentInput, selected.CurrentLabel,
                followEnabled, writeOnlyEnabled, writeOnlyMonitorId,
                writeOnlyConfirmedInputs, writeOnlyAutomaticReady,
                message + (writeOnlyEnabled ? "；只写兼容模式已开启，切源结果无法读回验证。" : string.Empty)));
        return values;
    }

    public Task<DisplayOperationResult> SwitchToDeviceAsync(string deviceId, CancellationToken cancellationToken) =>
        SwitchToDeviceCoreAsync(deviceId, requireReadableDeparture: false, cancellationToken);

    public Task<DisplayOperationResult> SwitchToDeviceForPeerReturnAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        SwitchToDeviceCoreAsync(deviceId, requireReadableDeparture: true, cancellationToken);

    private async Task<DisplayOperationResult> SwitchToDeviceCoreAsync(
        string deviceId,
        bool requireReadableDeparture,
        CancellationToken cancellationToken)
    {
        using var modeLease = await EnterModeGateAsync(cancellationToken);
        var config = settings.Snapshot;
        if (requireReadableDeparture && config.DdcWriteOnlyEnabled)
        {
            return DepartureRejected(
                "Peer display return requires a fresh readable DDC/CI departure check; write-only compatibility mode is not eligible.");
        }
        StoredDisplayMapping? mapping;
        if (!config.DdcWriteOnlyEnabled)
        {
            var initialPlan = EvaluateNormalDeparture(config, deviceId, []);
            if (!initialPlan.ConfigurationValid)
                return DepartureRejected(initialPlan.Message);
            if (ProbeRequested is null)
                return DepartureRejected("DDC/CI 组件不可用，无法在切换键鼠前重新验证当前显示输入。");

            var freshDisplays = await ProbeUnderModeGateAsync(cancellationToken);
            config = settings.Snapshot;
            var freshPlan = EvaluateNormalDeparture(config, deviceId, freshDisplays);
            if (!freshPlan.CanDepart || freshPlan.TargetMapping is null)
                return DepartureRejected(freshPlan.Message);
            mapping = freshPlan.TargetMapping;
        }
        else
        {
            mapping = config.DisplayMappings.FirstOrDefault(value => value.DeviceId == deviceId &&
                string.Equals(value.MonitorId, config.DdcWriteOnlyMonitorId, StringComparison.OrdinalIgnoreCase));
        }
        if (mapping is null)
            return config.DdcWriteOnlyEnabled
                ? new DisplayOperationResult(true, false, false,
                    "只写兼容模式缺少目标设备映射，已保留本机键鼠控制。", true)
                : DepartureRejected("缺少目标设备显示器输入映射，已保留当前键鼠控制。");
        var switchHandler = config.DdcWriteOnlyEnabled ? CompatibilityWriteRequested : SwitchRequested;
        if (switchHandler is null)
            return new DisplayOperationResult(true, false, false, "DDC/CI 组件不可用，已保留本机键鼠控制。",
                config.DdcWriteOnlyEnabled);
        if (config.DdcWriteOnlyEnabled && !DisplayCompatibilitySettings.IsAutomaticReady(config, deviceId))
        {
            return new DisplayOperationResult(true, false, false,
                "只写模式尚未完成 HDMI1 与 DP 双向确认，或缺少本机/目标设备映射，已保留本机键鼠控制。", true);
        }
        if (config.DdcWriteOnlyEnabled &&
            (!string.Equals(mapping.MonitorId, config.DdcWriteOnlyMonitorId, StringComparison.OrdinalIgnoreCase) ||
             !IsWriteOnlyInputValue(mapping.VcpValue) ||
             !(config.DdcWriteOnlyConfirmedInputs ?? []).Contains(mapping.VcpValue)))
        {
            return new DisplayOperationResult(true, false, false,
                "该只写输入源尚未经过测试和目视确认，已保留本机键鼠控制。", true);
        }
        var result = await switchHandler(mapping.MonitorId, mapping.VcpValue, cancellationToken);
        var followEnabled = settings.Snapshot.PhysicalFollowEnabled;
        state.SetDisplay(state.Display with
        {
            Supported = result.CompatibilityMode ? state.Display.Supported : result.Attempted,
            Stable = result.Verified,
            CurrentInput = result.CompatibilityMode
                ? null
                : result.FocusMayProceed ? mapping.VcpValue : state.Display.CurrentInput,
            CurrentLabel = result.CompatibilityMode
                ? null
                : result.FocusMayProceed ? mapping.Label : state.Display.CurrentLabel,
            PhysicalFollowEnabled = followEnabled,
            WriteOnlyEnabled = config.DdcWriteOnlyEnabled,
            WriteOnlyMonitorId = config.DdcWriteOnlyMonitorId,
            WriteOnlyConfirmedInputs = [.. (config.DdcWriteOnlyConfirmedInputs ?? [])],
            WriteOnlyAutomaticReady = DisplayCompatibilitySettings.IsAutomaticReady(config),
            Message = result.Message + (result.CommandIssued && !result.Verified
                ? " 本输入端暂时失去 DDC/CI；实体跟随继续等待远端 Agent 确认。"
                : string.Empty)
        });
        return result;
    }

    public async Task<DisplayArrivalConfirmation> ConfirmLocalArrivalAsync(CancellationToken cancellationToken)
    {
        using var modeLease = await EnterModeGateAsync(cancellationToken);
        var config = settings.Snapshot;
        if (config.DdcWriteOnlyEnabled)
        {
            return new DisplayArrivalConfirmation(false, null, null, null,
                "只写兼容模式无法读取 VCP 0x60，不能自动确认画面已到达本机。");
        }
        if (ProbeRequested is null)
        {
            return new DisplayArrivalConfirmation(false, null, null, null,
                "DDC/CI 组件不可用，无法确认画面已到达本机。");
        }

        var freshDisplays = await ProbeUnderModeGateAsync(cancellationToken);
        var confirmation = EvaluateLocalArrival(settings.Snapshot, freshDisplays);
        state.SetDisplay(state.Display with { Message = confirmation.Message });
        return confirmation;
    }

    public DisplayPeerReturnReadiness GetPeerReturnReadiness(string peerDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerDeviceId);
        var config = settings.Snapshot;
        if (config.DdcWriteOnlyEnabled)
        {
            return new DisplayPeerReturnReadiness(false,
                "只写兼容模式无法 fresh 读取当前输入，不能承诺自动回切；请使用显示器实体键。");
        }
        var plan = EvaluateNormalDeparture(config, peerDeviceId, []);
        if (!plan.ConfigurationValid) return new DisplayPeerReturnReadiness(false, plan.Message);
        if (ProbeRequested is null || SwitchRequested is null)
        {
            return new DisplayPeerReturnReadiness(false,
                "DDC/CI 组件尚未就绪，目标设备无法承诺安全回切显示器。");
        }
        return new DisplayPeerReturnReadiness(true,
            "目标设备已配置本机与请求方的同屏映射；实际回切前仍会执行 fresh 探测。");
    }

    internal static NormalDisplayDepartureEvaluation EvaluateNormalDeparture(
        AgentSettings config,
        string targetDeviceId,
        IReadOnlyList<DisplayProbeView> freshDisplays)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
        ArgumentNullException.ThrowIfNull(freshDisplays);

        if (string.Equals(targetDeviceId, config.DeviceId, StringComparison.Ordinal))
        {
            return NormalDisplayDepartureEvaluation.Invalid(
                "目标设备是本机；请使用 fresh arrival 确认而不是发送 DDC departure 写命令。");
        }

        var targetMappings = config.DisplayMappings
            .Where(mapping => string.Equals(mapping.DeviceId, targetDeviceId, StringComparison.Ordinal))
            .ToArray();
        if (targetMappings.Length != 1)
        {
            return NormalDisplayDepartureEvaluation.Invalid(targetMappings.Length == 0
                ? "缺少目标设备显示器输入映射，已保留当前键鼠控制。"
                : "目标设备存在多个显示器输入映射，无法安全确定要切换的显示器。");
        }

        var target = targetMappings[0];
        var selfMappings = config.DisplayMappings.Where(mapping =>
            string.Equals(mapping.DeviceId, config.DeviceId, StringComparison.Ordinal) &&
            string.Equals(mapping.MonitorId, target.MonitorId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selfMappings.Length != 1)
        {
            return NormalDisplayDepartureEvaluation.Invalid(selfMappings.Length == 0
                ? "同一显示器缺少本机输入映射，无法证明当前画面属于本机。"
                : "同一显示器存在多个本机输入映射，无法安全确定当前输入。");
        }

        var self = selfMappings[0];
        if (self.VcpValue > byte.MaxValue || target.VcpValue > byte.MaxValue)
            return NormalDisplayDepartureEvaluation.Invalid("显示器输入映射超出 VCP 0x60 的有效字节范围。");
        if (self.VcpValue == target.VcpValue)
            return NormalDisplayDepartureEvaluation.Invalid("本机与目标设备映射到了相同输入值，拒绝分离画面与键鼠。");

        if (freshDisplays.Count == 0)
        {
            return new NormalDisplayDepartureEvaluation(
                true, false, self, target, null,
                "尚未取得 fresh DDC/CI 双样本探测结果。");
        }

        var observed = freshDisplays.Where(display =>
            string.Equals(display.MonitorId, target.MonitorId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (observed.Length != 1)
        {
            return new NormalDisplayDepartureEvaluation(
                true, false, self, target, null,
                observed.Length == 0
                    ? "fresh DDC/CI 探测未找到映射对应的显示器，未发送切源命令。"
                    : "fresh DDC/CI 探测返回了重复显示器身份，未发送切源命令。");
        }

        var display = observed[0];
        if (!display.Supported || !display.Stable || display.CurrentInput is null)
        {
            return new NormalDisplayDepartureEvaluation(
                true, false, self, target, display,
                "当前显示器未通过 fresh 双样本稳定性检查，未发送切源命令。");
        }
        if (display.CurrentInput.Value != self.VcpValue)
        {
            return new NormalDisplayDepartureEvaluation(
                true, false, self, target, display,
                $"当前输入 0x{display.CurrentInput.Value:X2} 与本机映射 0x{self.VcpValue:X2} 不一致，未发送切源命令。");
        }

        return new NormalDisplayDepartureEvaluation(
            true, true, self, target, display,
            "fresh DDC/CI 探测已确认当前画面属于本机，可以发送 departure 切源命令。");
    }

    internal static DisplayArrivalConfirmation EvaluateLocalArrival(
        AgentSettings config,
        IReadOnlyList<DisplayProbeView> freshDisplays)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(freshDisplays);

        var selfMappings = config.DisplayMappings
            .Where(mapping => string.Equals(mapping.DeviceId, config.DeviceId, StringComparison.Ordinal))
            .ToArray();
        if (selfMappings.Length != 1)
        {
            return new DisplayArrivalConfirmation(false, null, null, null,
                selfMappings.Length == 0
                    ? "缺少本机显示器输入映射，无法确认画面到达。"
                    : "本机存在多个显示器输入映射，无法安全确定要确认的显示器。");
        }

        var self = selfMappings[0];
        var observed = freshDisplays.Where(display =>
            string.Equals(display.MonitorId, self.MonitorId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (observed.Length != 1)
        {
            return new DisplayArrivalConfirmation(false, self.MonitorId, self.VcpValue, null,
                observed.Length == 0
                    ? "fresh DDC/CI 探测未找到本机映射对应的显示器。"
                    : "fresh DDC/CI 探测返回了重复显示器身份，无法确认画面到达。");
        }

        var display = observed[0];
        if (!display.Supported || !display.Stable || display.CurrentInput is null)
        {
            return new DisplayArrivalConfirmation(false, self.MonitorId, self.VcpValue, display.CurrentInput,
                "显示器未通过 fresh 双样本稳定性检查，画面到达尚未确认。");
        }
        if (display.CurrentInput.Value != self.VcpValue)
        {
            return new DisplayArrivalConfirmation(false, self.MonitorId, self.VcpValue, display.CurrentInput,
                $"当前输入 0x{display.CurrentInput.Value:X2} 与本机映射 0x{self.VcpValue:X2} 不一致，画面到达尚未确认。");
        }

        return new DisplayArrivalConfirmation(true, self.MonitorId, self.VcpValue, display.CurrentInput,
            "fresh DDC/CI 双样本已确认画面到达本机。");
    }

    private DisplayOperationResult DepartureRejected(string message)
    {
        state.SetDisplay(state.Display with
        {
            Stable = false,
            CurrentInput = null,
            CurrentLabel = null,
            Message = message
        });
        return new DisplayOperationResult(true, false, false, message);
    }

    public async Task<object> SaveMappingAsync(DisplayMappingRequest request, CancellationToken cancellationToken)
    {
        using var modeLease = await EnterModeGateAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.MonitorId) || string.IsNullOrWhiteSpace(request.DeviceId))
            throw new ArgumentException("显示器和设备不能为空。");
        if (request.VcpValue > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request), "VCP 输入值必须位于 0 到 255 之间。");
        var config = settings.Snapshot;
        if (config.DdcWriteOnlyEnabled &&
            (!string.Equals(request.MonitorId, config.DdcWriteOnlyMonitorId, StringComparison.OrdinalIgnoreCase) ||
             !IsWriteOnlyInputValue(request.VcpValue) ||
             !(config.DdcWriteOnlyConfirmedInputs ?? []).Contains(request.VcpValue)))
            throw new InvalidOperationException("请先测试该输入值，并目视确认显示器确实切换成功后再保存映射。");
        var item = new StoredDisplayMapping(request.MonitorId, request.DeviceId, request.Label, request.VcpValue);
        var updatedSettings = await settings.UpdateAsync(current => current with
        {
            DisplayMappings = [.. current.DisplayMappings.Where(value => !(value.MonitorId == item.MonitorId && value.DeviceId == item.DeviceId)), item]
        }, cancellationToken);
        RotateFollowPublisherGeneration();
        state.SetDisplay(state.Display with
        {
            WriteOnlyAutomaticReady = DisplayCompatibilitySettings.IsAutomaticReady(updatedSettings),
            Message = updatedSettings.DdcWriteOnlyEnabled
                ? "只写输入映射已保存；命令尚未发送，且无法通过读取验证。"
                : state.Display.Message
        });
        return item;
    }

    public async Task<DisplayView> SetPhysicalFollowAsync(bool enabled, CancellationToken cancellationToken)
    {
        using var modeLease = await EnterModeGateAsync(cancellationToken);
        DisplayProbeView? stable = null;
        if (enabled)
        {
            if (settings.Snapshot.DdcWriteOnlyEnabled)
                throw new InvalidOperationException("只写兼容模式无法读取当前输入源，因此不能开启实体按钮跟随。");
            stable = Snapshot().FirstOrDefault(static value => value.Supported && value.Stable)
                ?? throw new InvalidOperationException("请先完成 DDC/CI 稳定性探测。");
            var followConfig = settings.Snapshot;
            var selfMappings = followConfig.DisplayMappings.Where(value =>
                string.Equals(value.MonitorId, stable.MonitorId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(value.DeviceId, followConfig.DeviceId, StringComparison.Ordinal)).ToArray();
            if (selfMappings.Length != 1)
                throw new InvalidOperationException(
                    "Physical follow requires exactly one local-device mapping on the selected monitor.");
            var pairedIds = followConfig.Peers.Select(static peer => peer.Id).ToHashSet(StringComparer.Ordinal);
            if (!followConfig.DisplayMappings.Any(value =>
                    string.Equals(value.MonitorId, stable!.MonitorId, StringComparison.OrdinalIgnoreCase) &&
                    pairedIds.Contains(value.DeviceId) &&
                    value.VcpValue != selfMappings[0].VcpValue))
                throw new InvalidOperationException(
                    "Physical follow requires a paired peer mapped to a different input on the same monitor.");

            var mappings = followConfig.DisplayMappings
                .Where(value => string.Equals(value.MonitorId, stable.MonitorId, StringComparison.OrdinalIgnoreCase))
                .Select(static value => value.VcpValue)
                .Distinct()
                .Take(2)
                .Count();
            if (mappings < 2)
                throw new InvalidOperationException("请先为同一显示器保存 HDMI1 与 DP 两个不同输入源映射。");
        }

        await settings.UpdateAsync(current =>
        {
            if (enabled && current.DdcWriteOnlyEnabled)
                throw new InvalidOperationException("只写兼容模式无法读取当前输入源，因此不能开启实体按钮跟随。");
            if (enabled)
            {
                var localMappings = current.DisplayMappings.Where(value =>
                    string.Equals(value.MonitorId, stable!.MonitorId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(value.DeviceId, current.DeviceId, StringComparison.Ordinal)).ToArray();
                var currentPairedIds = current.Peers.Select(static peer => peer.Id)
                    .ToHashSet(StringComparer.Ordinal);
                if (localMappings.Length != 1 || !current.DisplayMappings.Any(value =>
                        string.Equals(value.MonitorId, stable!.MonitorId, StringComparison.OrdinalIgnoreCase) &&
                        currentPairedIds.Contains(value.DeviceId) &&
                        value.VcpValue != localMappings[0].VcpValue))
                    throw new InvalidOperationException(
                        "Physical-follow mappings or paired-peer trust changed while enabling the feature.");
            }
            return current with { PhysicalFollowEnabled = enabled };
        }, cancellationToken);
        RotateFollowPublisherGeneration();
        var updated = state.Display with
        {
            PhysicalFollowEnabled = enabled,
            Message = enabled
                ? "实体按钮跟随已开启；只应在实际连接键鼠的电脑开启，另一台会自动作为发布端。连续两次稳定读数后才会切换键鼠。"
                : "实体按钮跟随已关闭。"
        };
        state.SetDisplay(updated);
        return updated;
    }

    public async Task<IDisposable?> TryAcquirePhysicalFollowLeaseAsync(
        string targetDeviceId,
        uint observedInput,
        CancellationToken cancellationToken)
        => await TryAcquirePhysicalFollowLeaseCoreAsync(
            targetDeviceId, monitorId: null, observedInput, cancellationToken);

    public async Task<IDisposable?> TryAcquirePhysicalFollowLeaseAsync(
        string targetDeviceId,
        string monitorId,
        uint observedInput,
        CancellationToken cancellationToken)
        => await TryAcquirePhysicalFollowLeaseCoreAsync(
            targetDeviceId, monitorId, observedInput, cancellationToken);

    public async Task<IDisposable?> TryAcquirePhysicalFollowLeaseAsync(
        string targetDeviceId,
        CancellationToken cancellationToken)
        => await TryAcquirePhysicalFollowLeaseCoreAsync(
            targetDeviceId, monitorId: null, observedInput: null, cancellationToken);

    public async Task<DisplayFollowPublishPermit?> TryAcquirePhysicalFollowPublisherLeaseAsync(
        string subscriberDeviceId,
        string monitorId,
        uint observedSelfInput,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriberDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        using var modeLease = await EnterModeGateAsync(cancellationToken);
        var config = settings.Snapshot;
        var exactSelfMapping = config.DisplayMappings.Count(value =>
            string.Equals(value.DeviceId, config.DeviceId, StringComparison.Ordinal) &&
            string.Equals(value.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase) &&
            value.VcpValue == observedSelfInput) == 1;
        var readiness = GetPeerReturnReadiness(subscriberDeviceId);
        if (!config.PhysicalFollowEnabled && !config.DdcWriteOnlyEnabled &&
            exactSelfMapping && readiness.Ready)
        {
            lock (_followPublisherGenerationGate)
                return new DisplayFollowPublishPermit(_followPublisherGeneration.Token);
        }
        return null;
    }

    public async Task<DisplayFollowObservationPermit?> TryCreatePhysicalFollowObservationPermitAsync(
        string targetDeviceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
        using var modeLease = await EnterModeGateAsync(cancellationToken);
        var config = settings.Snapshot;
        if (!config.PhysicalFollowEnabled || config.DdcWriteOnlyEnabled ||
            !config.DisplayMappings.Any(mapping =>
                string.Equals(mapping.DeviceId, targetDeviceId, StringComparison.Ordinal)))
            return null;
        lock (_followPublisherGenerationGate)
            return new DisplayFollowObservationPermit(_followPublisherGeneration.Token);
    }

    private async Task<IDisposable?> TryAcquirePhysicalFollowLeaseCoreAsync(
        string targetDeviceId,
        string? monitorId,
        uint? observedInput,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
        var modeLease = await EnterModeGateAsync(cancellationToken);
        var config = settings.Snapshot;
        if (!config.PhysicalFollowEnabled || config.DdcWriteOnlyEnabled)
        {
            modeLease.Dispose();
            return null;
        }
        var mapping = config.DisplayMappings.FirstOrDefault(value =>
            value.DeviceId == targetDeviceId &&
            (monitorId is null || string.Equals(value.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase)) &&
            (observedInput is null || value.VcpValue == observedInput.Value));
        if (mapping is not null) return modeLease;
        modeLease.Dispose();
        return null;
    }

    public async Task<DisplayView> SetWriteOnlyCompatibilityAsync(
        DisplayCompatibilityRequest request,
        CancellationToken cancellationToken)
    {
        using var modeLease = await EnterModeGateAsync(cancellationToken);
        if (!request.Enabled)
        {
            await settings.UpdateAsync(current => current with
            {
                DdcWriteOnlyEnabled = false,
                DdcWriteOnlyMonitorId = null,
                DdcWriteOnlyConfirmedInputs = []
            }, cancellationToken);
            RotateFollowPublisherGeneration();
            lock (_gate) _pendingCompatibilityTest = null;
            var disabled = state.Display with
            {
                WriteOnlyEnabled = false,
                WriteOnlyMonitorId = null,
                WriteOnlyConfirmedInputs = [],
                WriteOnlyAutomaticReady = false,
                Message = "只写 DDC 兼容模式已关闭。"
            };
            state.SetDisplay(disabled);
            return disabled;
        }

        if (!request.AcknowledgeRisk)
            throw new InvalidOperationException("请先确认两台电脑都在输出信号，并确保显示器实体按键可用于恢复画面。");
        if (string.IsNullOrWhiteSpace(request.MonitorId))
            throw new ArgumentException("请选择要绑定的显示器。", nameof(request));
        var selected = Snapshot().FirstOrDefault(value =>
            string.Equals(value.MonitorId, request.MonitorId, StringComparison.OrdinalIgnoreCase));
        if (selected is null || !selected.WriteOnlyEligible)
            throw new InvalidOperationException("该显示器尚未完成身份探测，不能启用只写兼容模式。");

        await settings.UpdateAsync(current => current with
        {
            DdcWriteOnlyEnabled = true,
            DdcWriteOnlyMonitorId = selected.MonitorId,
            DdcWriteOnlyConfirmedInputs = [],
            PhysicalFollowEnabled = false
        }, cancellationToken);
        RotateFollowPublisherGeneration();
        lock (_gate)
        {
            _pendingCompatibilityTest = null;
            _displays = [.. _displays.Select(display =>
                string.Equals(display.MonitorId, selected.MonitorId, StringComparison.OrdinalIgnoreCase)
                    ? display with { Stable = false, CurrentInput = null, CurrentLabel = null }
                    : display)];
        }
        var enabled = state.Display with
        {
            CurrentInput = null,
            CurrentLabel = null,
            Stable = false,
            PhysicalFollowEnabled = false,
            WriteOnlyEnabled = true,
            WriteOnlyMonitorId = selected.MonitorId,
            WriteOnlyConfirmedInputs = [],
            WriteOnlyAutomaticReady = false,
            Message = "只写 DDC 兼容模式已开启；必须逐项测试并目视确认后，输入值才能用于自动切换。"
        };
        state.SetDisplay(enabled);
        return enabled;
    }

    public async Task<DisplayOperationResult> TestWriteOnlyAsync(
        DisplayCompatibilityTestRequest request,
        CancellationToken cancellationToken)
    {
        using var modeLease = await EnterModeGateAsync(cancellationToken);
        if (!request.AcknowledgeRisk)
            throw new InvalidOperationException("请先确认可能出现短暂黑屏，并确保显示器实体按键可用于恢复画面。");
        if (!IsWriteOnlyInputValue(request.VcpValue))
            throw new ArgumentOutOfRangeException(nameof(request), "兼容测试只允许 DP 0x0F 或 HDMI1 0x11。");
        var config = settings.Snapshot;
        if (!config.DdcWriteOnlyEnabled ||
            !string.Equals(config.DdcWriteOnlyMonitorId, request.MonitorId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请先为当前显示器开启只写 DDC 兼容模式。");
        lock (_gate)
        {
            if (_pendingCompatibilityTest is { } existing &&
                DateTimeOffset.UtcNow - existing.CreatedAt <= TimeSpan.FromMinutes(2))
                throw new InvalidOperationException("请先确认上一项兼容测试的目视结果，再测试另一个输入源。");
            _pendingCompatibilityTest = null;
        }
        if (CompatibilityWriteRequested is null)
            return new DisplayOperationResult(true, false, false, "DDC/CI 组件不可用，未发送测试命令。", true);

        var reservationId = Guid.NewGuid().ToString("N");
        string? confirmationId = reservationId;
        lock (_gate)
        {
            _pendingCompatibilityTest = new PendingCompatibilityTest(
                reservationId, request.MonitorId, request.VcpValue, DateTimeOffset.UtcNow);
        }
        DisplayOperationResult result;
        try
        {
            result = await CompatibilityWriteRequested(request.MonitorId, request.VcpValue, cancellationToken);
        }
        catch
        {
            ClearPendingTest(reservationId);
            throw;
        }
        if (!result.CommandIssued)
        {
            ClearPendingTest(reservationId);
            confirmationId = null;
        }
        state.SetDisplay(state.Display with
        {
            Stable = false,
            PhysicalFollowEnabled = false,
            Message = result.Message
        });
        return result with { ConfirmationId = confirmationId };
    }

    public async Task<DisplayView> ConfirmWriteOnlyTestAsync(
        DisplayCompatibilityTestConfirmation request,
        CancellationToken cancellationToken)
    {
        using var modeLease = await EnterModeGateAsync(cancellationToken);
        PendingCompatibilityTest? pending;
        lock (_gate)
        {
            pending = _pendingCompatibilityTest;
            if (pending is null)
                throw new InvalidOperationException("没有等待确认的兼容测试，请重新发送一次测试命令。");
            if (DateTimeOffset.UtcNow - pending.CreatedAt > TimeSpan.FromMinutes(2))
            {
                _pendingCompatibilityTest = null;
                throw new InvalidOperationException("兼容测试确认已超时，请重新发送一次测试命令。");
            }
            if (!string.Equals(pending.Id, request.ConfirmationId, StringComparison.Ordinal) ||
                !string.Equals(pending.MonitorId, request.MonitorId, StringComparison.OrdinalIgnoreCase) ||
                pending.VcpValue != request.VcpValue)
                throw new InvalidOperationException("确认信息与当前等待确认的兼容测试不匹配。");
            _pendingCompatibilityTest = null;
        }

        var updatedSettings = await settings.UpdateAsync(current =>
        {
            if (!current.DdcWriteOnlyEnabled ||
                !string.Equals(current.DdcWriteOnlyMonitorId, request.MonitorId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("只写兼容模式已关闭或绑定显示器已变化。");
            var confirmed = new HashSet<uint>(current.DdcWriteOnlyConfirmedInputs ?? []);
            if (request.Worked) confirmed.Add(request.VcpValue);
            else confirmed.Remove(request.VcpValue);
            return current with { DdcWriteOnlyConfirmedInputs = [.. confirmed.Order()] };
        }, cancellationToken);

        var updated = state.Display with
        {
            WriteOnlyConfirmedInputs = [.. (updatedSettings.DdcWriteOnlyConfirmedInputs ?? [])],
            WriteOnlyAutomaticReady = DisplayCompatibilitySettings.IsAutomaticReady(updatedSettings),
            Message = request.Worked
                ? $"已目视确认输入值 0x{request.VcpValue:X2}；现在可以保存对应设备映射。"
                : $"输入值 0x{request.VcpValue:X2} 未通过目视确认，不会用于自动切换。"
        };
        state.SetDisplay(updated);
        return updated;
    }

    internal static bool IsWriteOnlyInputValue(uint value) =>
        DisplayCompatibilitySettings.IsInputValueAllowed(value);

    private async Task<ModeGateLease> EnterModeGateAsync(CancellationToken cancellationToken)
    {
        await _modeGate.WaitAsync(cancellationToken);
        return new ModeGateLease(_modeGate);
    }

    private void RotateFollowPublisherGeneration()
    {
        CancellationTokenSource previous;
        lock (_followPublisherGenerationGate)
        {
            previous = _followPublisherGeneration;
            _followPublisherGeneration = new CancellationTokenSource();
        }
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ClearPendingTest(string confirmationId)
    {
        lock (_gate)
        {
            if (string.Equals(_pendingCompatibilityTest?.Id, confirmationId, StringComparison.Ordinal))
                _pendingCompatibilityTest = null;
        }
    }

    private sealed record PendingCompatibilityTest(string Id, string MonitorId, uint VcpValue, DateTimeOffset CreatedAt);

    private sealed class ModeGateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

public sealed record DisplayProbeView(string MonitorId, string Name, bool Supported, bool Stable, uint? CurrentInput,
    string? CurrentLabel, string Message, bool WriteOnlyEligible, IReadOnlyList<StoredDisplayMapping> Mappings);
public sealed record DisplayOperationResult(bool Attempted, bool FocusMayProceed, bool Verified, string Message,
    bool CompatibilityMode = false, bool CommandIssued = false, string? ConfirmationId = null);
public sealed record DisplayArrivalConfirmation(
    bool Confirmed,
    string? MonitorId,
    uint? ExpectedInput,
    uint? ObservedInput,
    string Message);
public sealed record DisplayPeerReturnReadiness(bool Ready, string Message);
public sealed record DisplayFollowPublishPermit(CancellationToken ConfigurationToken);
public sealed record DisplayFollowObservationPermit(CancellationToken ConfigurationToken);
public sealed record DisplayMappingRequest(string MonitorId, string DeviceId, string Label, uint VcpValue);
public sealed record DisplayCompatibilityRequest(bool Enabled, string? MonitorId, bool AcknowledgeRisk);
public sealed record DisplayCompatibilityTestRequest(string MonitorId, uint VcpValue, bool AcknowledgeRisk);
public sealed record DisplayCompatibilityTestConfirmation(
    string ConfirmationId,
    string MonitorId,
    uint VcpValue,
    bool Worked);

internal sealed record NormalDisplayDepartureEvaluation(
    bool ConfigurationValid,
    bool CanDepart,
    StoredDisplayMapping? SelfMapping,
    StoredDisplayMapping? TargetMapping,
    DisplayProbeView? ObservedDisplay,
    string Message)
{
    internal static NormalDisplayDepartureEvaluation Invalid(string message) =>
        new(false, false, null, null, null, message);
}
