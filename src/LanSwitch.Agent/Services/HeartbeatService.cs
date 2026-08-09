using System.Diagnostics;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

public sealed class HeartbeatService(
    PeerDirectory peers,
    PeerHttpClientFactory clients,
    FocusCoordinator focus,
    AppState state,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var peer in peers.PairedPeers)
            {
                try
                {
                    var timer = Stopwatch.StartNew();
                    using var client = clients.Create(peer);
                    client.Timeout = RequestTimeout;
                    using var response = await client.GetAsync("peer/v1/heartbeat", stoppingToken);
                    response.EnsureSuccessStatusCode();
                    peers.MarkHeartbeat(peer.Id, timer.Elapsed.TotalMilliseconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException exception)
                {
                    await HandleOfflineAsync(peer, exception, stoppingToken);
                }
                catch (Exception exception)
                {
                    await HandleOfflineAsync(peer, exception, stoppingToken);
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }

    private async Task HandleOfflineAsync(RuntimePeer peer, Exception exception, CancellationToken stoppingToken)
    {
        var wasOnline = peer.Online;
        peers.MarkOffline(peer.Id);
        if (wasOnline)
        {
            state.AddDiagnostic("error", "网络心跳",
                $"{peer.Name} 心跳失败：{exception.GetType().Name}：{exception.Message}");
        }
        logger.LogDebug(exception, "设备 {Peer} 心跳失败", peer.Name);
        if (state.Focus.IsRemote && state.Focus.ActiveDeviceId == peer.Id)
            await focus.EmergencyReleaseAsync("网络连接中断", stoppingToken);
    }
}
