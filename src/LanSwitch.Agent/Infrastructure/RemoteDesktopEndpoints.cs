using System.Net.WebSockets;
using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Infrastructure;

public static class RemoteDesktopEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/remote-desktop", (SettingsStore settings) =>
            Results.Ok(RemoteDesktopConfiguration.GetView(settings.Snapshot)));
        app.MapPut("/api/v1/remote-desktop", async (
            RemoteDesktopSettingsUpdateRequest request,
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            RemoteDesktopConfiguration.Validate(request.FramesPerSecond, request.JpegQuality);
            var updated = await settings.UpdateAsync(current => current with
            {
                RemoteDesktopEnabled = request.Enabled,
                RemoteDesktopFramesPerSecond = request.FramesPerSecond,
                RemoteDesktopJpegQuality = request.JpegQuality
            }, cancellationToken);
            return Results.Ok(RemoteDesktopConfiguration.GetView(updated));
        });
        app.MapGet("/api/v1/remote-desktop/displays", async (
            string targetDeviceId,
            RemoteDesktopService desktop,
            CancellationToken cancellationToken) =>
            Results.Ok(await desktop.GetRemoteDisplayCatalogAsync(targetDeviceId, cancellationToken)));
        app.Map("/api/v1/remote-desktop/stream", HandleBrowserStreamAsync);

        app.MapGet("/peer/v1/remote-desktop/displays", (
            HttpContext context,
            SettingsStore settings,
            PeerDirectory peers,
            RemoteDesktopService desktop) =>
        {
            _ = RequireAuthorizedPeer(context, peers);
            return settings.Snapshot.RemoteDesktopEnabled
                ? Results.Ok(desktop.GetLocalDisplays())
                : Results.Conflict(new { error = "远端设备未允许查看本机桌面。" });
        });
        app.Map("/peer/v1/remote-desktop/stream", HandlePeerStreamAsync);
    }

    private static async Task HandleBrowserStreamAsync(HttpContext context)
    {
        var state = context.RequestServices.GetRequiredService<AppState>();
        if (!IsWebSocketRequest(context, state.CsrfToken) ||
            string.IsNullOrWhiteSpace(context.Request.Query["targetDeviceId"]) ||
            !int.TryParse(context.Request.Query["displayIndex"], out var displayIndex) || displayIndex < 0 ||
            !long.TryParse(context.Request.Query["generation"], out var generation) || generation < 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var targetDeviceId = context.Request.Query["targetDeviceId"].ToString();
        using var socket = await context.WebSockets.AcceptWebSocketAsync(RemoteDesktopProtocol.SubProtocol);
        try
        {
            var desktop = context.RequestServices.GetRequiredService<RemoteDesktopService>();
            await desktop.ProxyAsync(
                socket,
                targetDeviceId,
                displayIndex,
                generation,
                context.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state.AddDiagnostic("error", "远程桌面", $"远程画面连接失败：{exception.Message}");
            await CloseSafelyAsync(socket, WebSocketCloseStatus.InternalServerError, "远程桌面连接失败。");
        }
    }

    private static async Task HandlePeerStreamAsync(HttpContext context)
    {
        var peers = context.RequestServices.GetRequiredService<PeerDirectory>();
        var peer = RequireAuthorizedPeer(context, peers);
        var settings = context.RequestServices.GetRequiredService<SettingsStore>();
        if (!settings.Snapshot.RemoteDesktopEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = "本机未允许已配对设备查看桌面。" });
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest ||
            !context.WebSockets.WebSocketRequestedProtocols.Contains(RemoteDesktopProtocol.SubProtocol) ||
            !int.TryParse(context.Request.Query["displayIndex"], out var displayIndex) || displayIndex < 0 ||
            !RemoteDesktopProtocol.IsValidSessionId(context.Request.Query["desktopSessionId"]) ||
            !peers.TryGetTrustToken(peer.Id, peer.Fingerprint, out var trustToken))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(RemoteDesktopProtocol.SubProtocol);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, trustToken);
        var desktop = context.RequestServices.GetRequiredService<RemoteDesktopService>();
        try
        {
            await desktop.ServeAsync(
                socket,
                peer,
                displayIndex,
                context.Request.Query["desktopSessionId"].ToString(),
                linked.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var state = context.RequestServices.GetRequiredService<AppState>();
            state.AddDiagnostic("error", "远程桌面", $"向 {peer.Name} 提供远程画面失败：{exception.Message}");
            await CloseSafelyAsync(socket, WebSocketCloseStatus.InternalServerError, "远程桌面捕获或输入失败。");
        }
    }

    private static bool IsWebSocketRequest(HttpContext context, string expectedToken) =>
        context.WebSockets.IsWebSocketRequest &&
        context.WebSockets.WebSocketRequestedProtocols.Contains(RemoteDesktopProtocol.SubProtocol) &&
        string.Equals(context.Request.Query["token"], expectedToken, StringComparison.Ordinal);

    private static RuntimePeer RequireAuthorizedPeer(HttpContext context, PeerDirectory peers)
    {
        if (context.Items["LanSwitch.Peer"] is not RuntimePeer peer ||
            !peers.IsAuthorized(peer.Id, peer.Fingerprint))
            throw new UnauthorizedAccessException("设备未配对或信任已撤销。");
        return peer;
    }

    private static async Task CloseSafelyAsync(WebSocket socket, WebSocketCloseStatus status, string message)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try { await socket.CloseOutputAsync(status, message, CancellationToken.None); }
        catch { }
    }
}
