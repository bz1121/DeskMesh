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
        app.MapPut("/api/v1/admin/uac-bridge/settings", async (
            PrivilegedBridgeSettingsRequest request,
            SettingsStore settings,
            PrivilegedBridgeManager manager,
            CancellationToken cancellationToken) =>
        {
            await settings.UpdateAsync(
                current => current with
                {
                    LockedSessionControlEnabled = request.LockedSessionControlEnabled
                },
                cancellationToken);
            return Results.Ok(manager.GetStatus());
        });
    }
}

public sealed record PrivilegedBridgeSettingsRequest(bool LockedSessionControlEnabled);
