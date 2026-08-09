using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Infrastructure;

public static class AudioEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/audio", (AudioRelayService audio) => Results.Ok(audio.GetStatus()));

        app.MapPut("/api/v1/audio", async (
            AudioSettingsUpdateRequest request,
            SettingsStore settings,
            AudioRelayService audio,
            CancellationToken cancellationToken) =>
        {
            var volume = AudioConfiguration.ValidateVolume(request.Volume);
            await settings.UpdateAsync(current => current with
            {
                AudioForwardingEnabled = request.Enabled,
                AudioVolume = volume
            }, cancellationToken);
            return Results.Ok(audio.GetStatus());
        });

        app.Map("/peer/v1/audio/stream", HandlePeerAudioAsync);
    }

    private static async Task HandlePeerAudioAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (context.Items["LanSwitch.Peer"] is not RuntimePeer peer)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var settings = context.RequestServices.GetRequiredService<SettingsStore>();
        var input = context.RequestServices.GetRequiredService<InputCoordinator>();
        var desktop = context.RequestServices.GetRequiredService<RemoteDesktopService>();
        var directory = context.RequestServices.GetRequiredService<PeerDirectory>();
        if (!settings.Snapshot.AudioForwardingEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = "远端设备未启用音频跟随。" });
            return;
        }
        Func<bool> sessionIsActive;
        string sessionLabel;
        if (long.TryParse(context.Request.Query["epoch"], out var epoch) && epoch > 0)
        {
            sessionIsActive = () => input.IsIncomingCommitted(epoch, peer.Id);
            sessionLabel = $"键鼠 epoch={epoch}";
        }
        else
        {
            var desktopSessionId = context.Request.Query["desktopSessionId"].ToString();
            if (!RemoteDesktopProtocol.IsValidSessionId(desktopSessionId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "音频会话参数无效。" });
                return;
            }
            sessionIsActive = () => desktop.IsIncomingSessionActive(peer.Id, desktopSessionId);
            sessionLabel = $"远程桌面 {desktopSessionId[..8]}";
        }

        if (!sessionIsActive() ||
            !directory.TryGetTrustToken(peer.Id, peer.Fingerprint, out var trustToken))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = "音频请求与当前活动会话不匹配。" });
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync("lanswitch.audio.v1");
        var relay = context.RequestServices.GetRequiredService<AudioRelayService>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, trustToken);
        await relay.SendLoopbackAsync(socket, peer, sessionLabel, sessionIsActive, linked.Token);
    }
}
