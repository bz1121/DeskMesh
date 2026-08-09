namespace LanSwitch.Agent.Infrastructure;

public static class HotkeyEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/hotkeys", (SettingsStore settings) =>
            Results.Ok(HotkeyConfiguration.GetView(settings.Snapshot)));

        app.MapPut("/api/v1/hotkeys", async (
            HotkeyUpdateRequest request,
            SettingsStore settings,
            AppState state,
            CancellationToken cancellationToken) =>
        {
            var validated = HotkeyConfiguration.ValidateUserBindings(
                request.SwitchToLocal,
                request.ToggleRemote);
            var updated = await settings.UpdateAsync(current => current with
            {
                LocalHotkey = validated.SwitchToLocal,
                ToggleHotkey = validated.ToggleRemote,
                EmergencyHotkey = HotkeyConfiguration.EmergencyHotkey
            }, cancellationToken);
            var view = HotkeyConfiguration.GetView(updated);
            state.Publish("hotkeys", view);
            return Results.Ok(view);
        });
    }
}

public sealed record HotkeyUpdateRequest(string? SwitchToLocal, string? ToggleRemote);
