using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Tests;

public sealed class HotkeyConfigurationTests
{
    [Fact]
    public void ValidBindingsAreCanonicalizedAndEmergencyBindingStaysFixed()
    {
        var result = HotkeyConfiguration.ValidateUserBindings(
            "shift+ctrl+f8",
            "alt+ctrl+9");

        Assert.Equal("Ctrl+Shift+F8", result.SwitchToLocal);
        Assert.Equal("Ctrl+Alt+9", result.ToggleRemote);
        Assert.Equal("Ctrl+Alt+Shift+Esc", result.Emergency);
    }

    [Theory]
    [InlineData("F8", "Ctrl+Alt+F12")]
    [InlineData("Ctrl+F8", "Ctrl+Alt+F12")]
    [InlineData("Ctrl+Alt+Delete", "Ctrl+Alt+F12")]
    [InlineData("Win+Ctrl+F8", "Ctrl+Alt+F12")]
    [InlineData("Ctrl+Alt+F12", "Ctrl+Alt+F12")]
    [InlineData("Ctrl+Alt+Shift+Esc", "Ctrl+Alt+F12")]
    public void UnsafeOrDuplicateBindingsAreRejected(string local, string toggle)
    {
        Assert.Throws<ArgumentException>(() =>
            HotkeyConfiguration.ValidateUserBindings(local, toggle));
    }

    [Fact]
    public async Task SettingsPersistAndNotifyRuntimeListeners()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LanSwitch-hotkeys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = AgentOptions.Parse(["--data-dir", directory]);
            var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
            var settings = new SettingsStore(options, identity);
            AgentSettings? observed = null;
            settings.Changed += value => observed = value;

            await settings.UpdateAsync(current => current with
            {
                LocalHotkey = "Ctrl+Shift+F8",
                ToggleHotkey = "Ctrl+Alt+9",
                EmergencyHotkey = "Alt+F4"
            });

            Assert.NotNull(observed);
            Assert.Equal("Ctrl+Shift+F8", observed.LocalHotkey);
            Assert.Equal("Ctrl+Alt+9", observed.ToggleHotkey);
            Assert.Equal(HotkeyConfiguration.EmergencyHotkey, observed.EmergencyHotkey);

            var reloaded = new SettingsStore(options, identity).Snapshot;
            Assert.Equal("Ctrl+Shift+F8", reloaded.LocalHotkey);
            Assert.Equal("Ctrl+Alt+9", reloaded.ToggleHotkey);
            Assert.Equal(HotkeyConfiguration.EmergencyHotkey, reloaded.EmergencyHotkey);
        }
        finally
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var temporaryPath = settingsPath + ".new";
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            Directory.Delete(directory, recursive: false);
        }
    }
}
