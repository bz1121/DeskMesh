using LanSwitch.Agent.Infrastructure;
using LanSwitch.Windows.Display;
using LanSwitch.Windows.Input;

namespace LanSwitch.Agent.Services;

public sealed class WindowsIntegrationService : IHostedService, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly AppState _state;
    private readonly FocusCoordinator _focus;
    private readonly InputCoordinator _input;
    private readonly DisplayCoordinator _display;
    private readonly ILogger<WindowsIntegrationService> _logger;
    private readonly IInputHook _hook;
    private readonly IRawMouseInputMonitor _rawMouse = new WindowsRawMouseInputMonitor();
    private readonly IWindowsInputInjector _injector = new WindowsInputInjector();
    private readonly IDdcMonitorController _ddc = new WindowsDdcMonitorController();
    private IReadOnlyList<DdcMonitorProbeResult> _lastProbe = [];
    private IReadOnlyList<DdcMonitorIdentity> _compatibilityTargets = [];
    private readonly object _hotkeyGate = new();
    private HotkeySettingsView _activeHotkeys;
    private int _hookRestarting;

    public WindowsIntegrationService(SettingsStore settings, AppState state, FocusCoordinator focus,
        InputCoordinator input, DisplayCoordinator display, ILogger<WindowsIntegrationService> logger)
    {
        _settings = settings;
        _state = state;
        _focus = focus;
        _input = input;
        _display = display;
        _logger = logger;
        _activeHotkeys = HotkeyConfiguration.GetView(_settings.Snapshot);
        var hotkeys = HotkeyConfiguration.ToBindings(_settings.Snapshot);
        _hook = new WindowsLowLevelInputHook(new InputHookOptions
        {
            AllowSuppression = true,
            Hotkeys = hotkeys,
            IgnoreInjectedForHotkeys = true
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _injector.Start();
        _hook.KeyboardInput += OnKeyboardInput;
        _hook.MouseInput += OnMouseInput;
        _rawMouse.MouseMoved += OnRawMouseMoved;
        _rawMouse.Faulted += OnRawMouseFaulted;
        _hook.HotkeyPressed += OnHotkeyPressed;
        _hook.Faulted += OnHookFaulted;
        _input.RemoteBatchReceived += OnRemoteBatch;
        _input.ReleaseRequested += OnReleaseRequested;
        _display.ProbeRequested += ProbeDisplaysAsync;
        _display.SwitchRequested += SwitchDisplayAsync;
        _display.CompatibilityWriteRequested += SwitchDisplayCompatibilityAsync;
        _settings.Changed += OnSettingsChanged;
        _rawMouse.Start();
        _hook.Start();
        _state.Publish("notice", new { level = "info", message = "Windows 键鼠钩子已就绪；仅在远程控制时抑制本机事件。" });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.Changed -= OnSettingsChanged;
        _hook.Stop();
        _rawMouse.Stop();
        _injector.ReleaseAll();
        _injector.Stop();
        return Task.CompletedTask;
    }

    private void OnHookFaulted(object? sender, InputHookFaultedEventArgs args)
    {
        var callback = args.CallbackKind?.ToString() ?? "message-loop";
        var duration = args.CallbackDuration is { } elapsed
            ? $"{elapsed.TotalMilliseconds:F1} ms"
            : "未提供";
        var exception = args.Exception is null
            ? string.Empty
            : $"；{args.Exception.GetType().Name}：{args.Exception.Message}";
        var details = $"原因={args.Reason}，回调={callback}，耗时={duration}{exception}";
        var wasRemote = _state.Focus.IsRemote;
        if (wasRemote)
            _focus.EmergencyReleaseNow("键鼠钩子异常，已立即恢复本机输入");
        _state.AddDiagnostic(
            "error",
            "键鼠钩子",
            $"{details}；当时控制目标={(wasRemote ? "远端" : "本机")}，正在自动重启。");
        _logger.LogWarning(args.Exception, "键鼠钩子故障：{Details}", details);
        _state.Publish("notice", new
        {
            level = "error",
            message = $"键鼠钩子异常（{details}），正在自动重启；托盘控制仍可用。"
        });
        if (Interlocked.Exchange(ref _hookRestarting, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                try { _hook.Stop(); } catch { }
                _hook.Start();
                _state.Publish("notice", new { level = "info", message = "键鼠钩子已自动恢复。" });
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "键鼠钩子自动重启失败");
                _state.Publish("notice", new { level = "error", message = "键鼠钩子无法自动恢复，请从托盘退出后重新启动 DeskMesh。" });
            }
            finally { Interlocked.Exchange(ref _hookRestarting, 0); }
        });
    }

    private void OnRawMouseFaulted(object? sender, RawMouseInputFaultedEventArgs args)
    {
        _focus.EmergencyReleaseNow("鼠标原始输入异常，已恢复本机控制");
        _logger.LogError(args.Exception, "Raw Input 鼠标监听已停止");
        _state.Publish("notice", new
        {
            level = "error",
            message = "鼠标原始输入监听异常，已恢复本机控制；请重新启动 DeskMesh。"
        });
    }

    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs args)
    {
        var switchMode = _settings.Snapshot.SwitchMode;
        switch (ResolveHotkeyRouting(args.Command, switchMode))
        {
            case HotkeyRoutingAction.EmergencyRelease:
                PublishSeamlessReleaseIfNeeded(switchMode, "emergency-hotkey");
                _focus.EmergencyReleaseNow(
                    "紧急快捷键",
                    restoreDisplay: _state.Focus.IsRemote || !SwitchModeConfiguration.IsSeamlessRemote(switchMode));
                return;
            case HotkeyRoutingAction.SwitchToPrimary:
                PublishSeamlessReleaseIfNeeded(switchMode, "local-hotkey");
                _focus.EmergencyReleaseNow(
                    "快捷键切回本机",
                    restoreDisplay: _state.Focus.IsRemote || !SwitchModeConfiguration.IsSeamlessRemote(switchMode));
                return;
            case HotkeyRoutingAction.RequestSeamlessRemote:
                _ = RunSafelyAsync(() => _focus.RequestUserToggleAsync("hotkey", CancellationToken.None));
                return;
            case HotkeyRoutingAction.SwitchDirectSignal:
                _ = RunSafelyAsync(() => _focus.SwitchAsync(null, CancellationToken.None));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(args.Command));
        }
    }

    private void PublishSeamlessReleaseIfNeeded(string switchMode, string source)
    {
        if (!SwitchModeConfiguration.IsSeamlessRemote(switchMode)) return;
        _state.Publish("seamless-release-request", new
        {
            mode = SwitchModeConfiguration.SeamlessRemote,
            source
        });
    }

    internal static HotkeyRoutingAction ResolveHotkeyRouting(HotkeyCommand command, string? switchMode) =>
        command switch
        {
            HotkeyCommand.EmergencyRelease => HotkeyRoutingAction.EmergencyRelease,
            HotkeyCommand.SwitchToPrimary => HotkeyRoutingAction.SwitchToPrimary,
            HotkeyCommand.SwitchToSecondary when SwitchModeConfiguration.IsSeamlessRemote(switchMode) =>
                HotkeyRoutingAction.RequestSeamlessRemote,
            HotkeyCommand.SwitchToSecondary => HotkeyRoutingAction.SwitchDirectSignal,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

    private void OnSettingsChanged(AgentSettings settings)
    {
        var next = HotkeyConfiguration.GetView(settings);
        lock (_hotkeyGate)
        {
            if (next == _activeHotkeys) return;
            _hook.UpdateHotkeys(HotkeyConfiguration.ToBindings(settings));
            _activeHotkeys = next;
        }
        _state.Publish("notice", new { level = "info", message = "全局切换快捷键已更新并立即生效。" });
    }

    private void OnKeyboardInput(object? sender, KeyboardHookEventArgs args)
    {
        if (args.IsInjected || !_state.Focus.IsRemote) return;
        var flags = (args.Transition == InputTransition.Up ? 1 : 0) | (args.IsExtended ? 2 : 0);
        args.Handled = _input.QueueLocalEvents([new InputEventPacket("keyboard", args.VirtualKey, args.ScanCode, flags, args.Timestamp)]);
        if (!args.Handled) _focus.EmergencyReleaseNow("输入队列拥塞，已恢复本机控制");
    }

    private void OnMouseInput(object? sender, MouseHookEventArgs args)
    {
        if (args.IsInjected || !_state.Focus.IsRemote) return;
        var packets = new List<InputEventPacket>(1);
        if (args.Button is { } button && args.Transition is { } transition)
            packets.Add(new InputEventPacket("mouse-button", (int)button, transition == InputTransition.Down ? 1 : 0, 0, args.Timestamp));
        if (args.WheelDelta != 0)
            packets.Add(new InputEventPacket("mouse-wheel", args.IsHorizontalWheel ? 1 : 0, args.WheelDelta, 0, args.Timestamp));
        if (packets.Count == 0) { args.Handled = true; return; }
        var queued = _input.QueueLocalEvents(packets);
        var transientMoveOnly = IsTransientMouseMoveOnly(packets);
        args.Handled = queued || transientMoveOnly;
        if (!queued && !transientMoveOnly)
            _focus.EmergencyReleaseNow("输入队列拥塞，已恢复本机控制");
    }

    private void OnRawMouseMoved(object? sender, RawMouseInputEventArgs args)
    {
        if (!_state.Focus.IsRemote || (args.DeltaX == 0 && args.DeltaY == 0)) return;
        _ = _input.QueueLocalEvents([
            new InputEventPacket(
                "mouse-move",
                args.DeltaX,
                args.DeltaY,
                0,
                unchecked((uint)Environment.TickCount))
        ]);
    }

    private void OnRemoteBatch(InputBatchPacket batch)
    {
        var failed = false;
        foreach (var packet in batch.Events)
        {
            var result = packet.Type switch
            {
                "keyboard" => _injector.SendKeyboard(new KeyboardInjection((ushort)packet.Code, (ushort)packet.Value,
                        (packet.Flags & 1) != 0 ? InputTransition.Up : InputTransition.Down,
                        UseScanCode: packet.Value != 0, IsExtended: (packet.Flags & 2) != 0)),
                "mouse-move" => _injector.SendMouseMove(packet.Code, packet.Value),
                "mouse-button" => _injector.SendMouseButton(new MouseButtonInjection((MouseButton)packet.Code,
                    packet.Value == 1 ? InputTransition.Down : InputTransition.Up)),
                "mouse-wheel" => _injector.SendMouseWheel((short)packet.Value, packet.Code == 1),
                _ => new InputInjectionResult(0, 0, 0)
            };
            failed |= result.Attempted > 0 && !result.Succeeded;
        }
        if (failed)
        {
            _input.FailIncoming();
            _state.Publish("notice", new { level = "error", message = "Windows 拒绝了部分输入注入，已释放全部远端按键。" });
        }
    }

    internal static bool IsTransientMouseMoveOnly(IReadOnlyList<InputEventPacket> packets) =>
        packets.Count > 0 && packets.All(static packet => packet.Type == "mouse-move" && packet.Flags == 0);

    private void OnReleaseRequested()
    {
        var result = _injector.ReleaseAll();
        for (var attempt = 0; result.Remaining > 0 && attempt < 2; attempt++) result = _injector.ReleaseAll();
        if (result.Remaining > 0)
            _state.Publish("notice", new { level = "error", message = $"仍有 {result.Remaining} 个按键未能释放，请按紧急快捷键并切回本机。" });
    }

    private async Task<IReadOnlyList<DisplayProbeView>> ProbeDisplaysAsync(CancellationToken cancellationToken)
    {
        var config = _settings.Snapshot;
        var mappings = config.DisplayMappings;
        if (config.DdcWriteOnlyEnabled)
        {
            _compatibilityTargets = await _ddc.EnumerateCompatibilityTargetsAsync(cancellationToken);
            _lastProbe = [];
            return _compatibilityTargets.Select(monitor => new DisplayProbeView(
                monitor.MonitorId,
                monitor.Description,
                false,
                false,
                null,
                null,
                "只写兼容模式仅重新确认显示器身份；未读取 VCP 0x60。",
                true,
                mappings.Where(mapping => mapping.MonitorId == monitor.MonitorId).ToArray())).ToArray();
        }

        _compatibilityTargets = [];
        _lastProbe = await _ddc.ProbeAsync(new DdcProbeOptions { SampleInterval = TimeSpan.FromMilliseconds(120) }, cancellationToken);
        return _lastProbe.Select(result =>
        {
            var current = result.SecondSample?.CurrentValue ?? result.FirstSample?.CurrentValue;
            var label = mappings.FirstOrDefault(mapping => mapping.MonitorId == result.Monitor.MonitorId && mapping.VcpValue == current)?.Label;
            var writeOnlyEligible = IsExplicitCompatibilityTarget(result.Monitor);
            return new DisplayProbeView(result.Monitor.MonitorId, result.Monitor.Description,
                result.Status is DdcProbeStatus.Ready or DdcProbeStatus.Unstable,
                result.IsReady, current, label, result.ErrorMessage ?? StatusMessage(result.Status), writeOnlyEligible,
                mappings.Where(mapping => mapping.MonitorId == result.Monitor.MonitorId).ToArray());
        }).ToArray();
    }

    private async Task<DisplayOperationResult> SwitchDisplayAsync(string monitorId, uint inputSource, CancellationToken cancellationToken)
    {
        if (!_lastProbe.Any(result => result.IsReady && result.Monitor.MonitorId == monitorId))
            await ProbeDisplaysAsync(cancellationToken);
        var result = await _ddc.WriteInputSourceAsync(monitorId, inputSource,
            new DdcProbeOptions { SampleInterval = TimeSpan.FromMilliseconds(150) }, cancellationToken);
        if (!CanReuseProbeAfterWrite(result))
        {
            // A successful native SetVCPFeature may immediately move the monitor to the
            // other input and make this Agent's DDC channel unreadable. Never let the
            // pre-departure probe authorize another write while this input is off-screen.
            _lastProbe = [];
        }
        return TranslateNormalWriteResult(result);
    }

    internal static bool CanReuseProbeAfterWrite(DdcWriteResult result) => !result.CommandWasIssued;

    internal static DisplayOperationResult TranslateNormalWriteResult(DdcWriteResult result) => result.Status switch
    {
        DdcWriteStatus.Verified => new DisplayOperationResult(true, true, true, "显示器输入源已切换并验证。",
            CommandIssued: true),
        DdcWriteStatus.AppliedButUnverified => new DisplayOperationResult(true, true, false,
            "切源命令已发送；切换后 DDC 通道不可读。", CommandIssued: true),
        _ when result.CommandWasIssued => new DisplayOperationResult(true, false, false,
            result.ErrorMessage ?? $"DDC 切源命令已发送，但结果不确定：{result.Status}", CommandIssued: true),
        _ => new DisplayOperationResult(true, false, false,
            result.ErrorMessage ?? $"DDC 切源失败：{result.Status}")
    };

    private async Task<DisplayOperationResult> SwitchDisplayCompatibilityAsync(
        string monitorId,
        uint inputSource,
        CancellationToken cancellationToken)
    {
        if (!DisplayCoordinator.IsWriteOnlyInputValue(inputSource))
        {
            return new DisplayOperationResult(true, false, false,
                "只写兼容模式拒绝了非白名单输入值。", true);
        }

        var selectedMonitor = _compatibilityTargets.FirstOrDefault(monitor =>
            string.Equals(monitor.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase))
            ?? _lastProbe.Select(static result => result.Monitor).FirstOrDefault(monitor =>
                string.Equals(monitor.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase));
        if (selectedMonitor is null || !IsExplicitCompatibilityTarget(selectedMonitor))
        {
            _compatibilityTargets = await _ddc.EnumerateCompatibilityTargetsAsync(cancellationToken);
            selectedMonitor = _compatibilityTargets.FirstOrDefault(monitor =>
                string.Equals(monitor.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase));
        }
        if (selectedMonitor is null || !IsExplicitCompatibilityTarget(selectedMonitor))
        {
            return new DisplayOperationResult(true, false, false,
                "无法重新确认显示器身份，未发送只写 DDC 命令。", true);
        }

        var compatibilityResult = await _ddc.WriteInputSourceCompatibilityAsync(
            selectedMonitor,
            inputSource,
            new DdcWriteOnlyCompatibilityOptions { AcknowledgeUnverifiedWriteRisk = true },
            cancellationToken);
        return compatibilityResult.CommandWasIssued
            ? new DisplayOperationResult(true, true, false,
                "只写 DDC 切源命令已发送；本模式不执行读回，因此无法验证实际结果。", true, true)
            : new DisplayOperationResult(true, false, false,
                compatibilityResult.ErrorMessage ?? $"只写 DDC 切源失败：{compatibilityResult.Status}", true);
    }

    private static bool IsExplicitCompatibilityTarget(DdcMonitorIdentity monitor) =>
        !string.Equals(monitor.MonitorId, "enumeration", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(monitor.DisplayDeviceName) &&
        !string.IsNullOrWhiteSpace(monitor.Description) &&
        monitor.TopologyFingerprint.Length == 32 &&
        monitor.TopologyFingerprint.All(Uri.IsHexDigit);

    private async Task RunSafelyAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "快捷键操作失败");
            _state.Publish("notice", new { level = "error", message = exception.Message });
        }
    }

    private static string StatusMessage(DdcProbeStatus status) => status switch
    {
        DdcProbeStatus.Ready => "DDC/CI 已通过双样本稳定性检查。",
        DdcProbeStatus.Unstable => "输入源读数不稳定，实体按钮跟随已停用。",
        DdcProbeStatus.Unsupported => "显示器未开放 DDC/CI VCP 0x60。",
        _ => "DDC/CI 探测失败。"
    };

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _hook.Faulted -= OnHookFaulted;
        _rawMouse.Faulted -= OnRawMouseFaulted;
        _hook.Dispose();
        _rawMouse.Dispose();
        _injector.Dispose();
        _ddc.Dispose();
    }
}

internal enum HotkeyRoutingAction
{
    SwitchToPrimary,
    SwitchDirectSignal,
    RequestSeamlessRemote,
    EmergencyRelease
}
