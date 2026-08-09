using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

public sealed class DisplayFollowWorker(
    SettingsStore settings,
    DisplayCoordinator display,
    FocusCoordinator focus,
    AppState state,
    ILogger<DisplayFollowWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        uint? candidate = null;
        var samples = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            if (!settings.Snapshot.PhysicalFollowEnabled || settings.Snapshot.DdcWriteOnlyEnabled ||
                state.Focus.Phase == "preparing") continue;
            try
            {
                var probes = await display.ProbeAsync(stoppingToken);
                var current = probes.FirstOrDefault(static value => value.Supported && value.Stable)?.CurrentInput;
                if (current is null) { candidate = null; samples = 0; continue; }
                if (candidate == current) samples++; else { candidate = current; samples = 1; }
                if (samples < 2) continue;
                var mapping = settings.Snapshot.DisplayMappings.FirstOrDefault(value => value.VcpValue == current.Value);
                if (mapping is null || mapping.DeviceId == state.Focus.ActiveDeviceId) continue;
                await focus.SwitchAsync(
                    mapping.DeviceId,
                    stoppingToken,
                    skipDisplay: true,
                    transitionGuardFactory: token =>
                        display.TryAcquirePhysicalFollowLeaseAsync(mapping.DeviceId, current.Value, token));
                samples = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException exception)
            {
                logger.LogDebug(exception, "实体信号源跟随切换已被本机安全操作取消");
                candidate = null;
                samples = 0;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "实体信号源跟随探测失败");
            }
        }
    }
}
