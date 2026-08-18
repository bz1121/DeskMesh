namespace LanSwitch.Agent.Infrastructure;

public static class PrivilegedBridgeEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/admin/uac-bridge", (PrivilegedBridgeManager manager) =>
            Results.Ok(manager.GetStatus()));
        app.MapPost("/api/v1/admin/uac-bridge/install", async (
            PrivilegedBridgeManager manager,
            CancellationToken cancellationToken) =>
            Results.Ok(await manager.InstallAsync(cancellationToken)));
        app.MapPost("/api/v1/admin/uac-bridge/uninstall", async (
            PrivilegedBridgeManager manager,
            CancellationToken cancellationToken) =>
            Results.Ok(await manager.UninstallAsync(cancellationToken)));
    }
}
