using System.Text.Json;
using System.Text.Json.Nodes;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;
using LanSwitch.Windows.Input;

namespace LanSwitch.Agent.Tests;

public sealed class SwitchModeConfigurationTests
{
    [Fact]
    public void NewInstallDefaultsToDirectSignal()
    {
        var settings = AgentSettings.CreateDefault("local");

        Assert.Equal(SwitchModeConfiguration.DirectSignal, settings.SwitchMode);
    }

    [Theory]
    [InlineData(SwitchModeConfiguration.DirectSignal, SwitchModeConfiguration.DirectSignal)]
    [InlineData(SwitchModeConfiguration.SeamlessRemote, SwitchModeConfiguration.SeamlessRemote)]
    [InlineData("futureMode", SwitchModeConfiguration.DirectSignal)]
    [InlineData(null, SwitchModeConfiguration.DirectSignal)]
    public void NormalizeUsesOnlyKnownModes(string? value, string expected)
    {
        Assert.Equal(expected, SwitchModeConfiguration.NormalizeValue(value));
    }

    [Fact]
    public void ApiValidationRejectsUnknownModes()
    {
        Assert.Equal(
            SwitchModeConfiguration.SeamlessRemote,
            SwitchModeConfiguration.Validate(SwitchModeConfiguration.SeamlessRemote));
        Assert.Throws<ArgumentException>(() => SwitchModeConfiguration.Validate("remote"));
    }

    [Fact]
    public void LegacySettingsWithoutSwitchModeRemainDirectSignal()
    {
        using var context = SettingsContext.Create();
        var json = JsonNode.Parse(JsonSerializer.Serialize(
            AgentSettings.CreateDefault(context.Identity.DeviceId),
            JsonOptions))!.AsObject();
        json.Remove("switchMode");
        File.WriteAllText(context.SettingsPath, json.ToJsonString(JsonOptions));

        var settings = new SettingsStore(context.Options, context.Identity);

        Assert.Equal(SwitchModeConfiguration.DirectSignal, settings.Snapshot.SwitchMode);
    }

    [Fact]
    public async Task SeamlessModePersistsAndAppearsInTopLevelStatus()
    {
        using var context = SettingsContext.Create();
        var settings = new SettingsStore(context.Options, context.Identity);
        await settings.UpdateAsync(current => current with
        {
            SwitchMode = SwitchModeConfiguration.SeamlessRemote
        });
        var state = new AppState(context.Identity, settings, context.Options);

        using var status = JsonDocument.Parse(JsonSerializer.Serialize(state.GetStatus(paired: false), JsonOptions));
        var reloaded = new SettingsStore(context.Options, context.Identity);

        Assert.Equal(
            SwitchModeConfiguration.SeamlessRemote,
            status.RootElement.GetProperty("switchMode").GetString());
        Assert.Equal(SwitchModeConfiguration.SeamlessRemote, reloaded.Snapshot.SwitchMode);
    }

    [Fact]
    public async Task UnknownPersistedModeFailsSafeToDirectSignal()
    {
        using var context = SettingsContext.Create();
        var settings = new SettingsStore(context.Options, context.Identity);

        var updated = await settings.UpdateAsync(current => current with { SwitchMode = "futureMode" });

        Assert.Equal(SwitchModeConfiguration.DirectSignal, updated.SwitchMode);
    }

    [Theory]
    [InlineData(HotkeyCommand.SwitchToSecondary, SwitchModeConfiguration.DirectSignal,
        nameof(HotkeyRoutingAction.SwitchDirectSignal))]
    [InlineData(HotkeyCommand.SwitchToSecondary, SwitchModeConfiguration.SeamlessRemote,
        nameof(HotkeyRoutingAction.RequestSeamlessRemote))]
    [InlineData(HotkeyCommand.SwitchToPrimary, SwitchModeConfiguration.SeamlessRemote,
        nameof(HotkeyRoutingAction.SwitchToPrimary))]
    [InlineData(HotkeyCommand.EmergencyRelease, SwitchModeConfiguration.SeamlessRemote,
        nameof(HotkeyRoutingAction.EmergencyRelease))]
    public void HotkeysRouteWithoutDowngradingSeamlessMode(
        HotkeyCommand command,
        string mode,
        string expected)
    {
        Assert.Equal(expected, WindowsIntegrationService.ResolveHotkeyRouting(command, mode).ToString());
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private sealed class SettingsContext : IDisposable
    {
        private SettingsContext(string directory, AgentOptions options, DeviceIdentity identity)
        {
            Directory = directory;
            Options = options;
            Identity = identity;
        }

        internal string Directory { get; }
        internal string SettingsPath => Path.Combine(Directory, "settings.json");
        internal AgentOptions Options { get; }
        internal DeviceIdentity Identity { get; }

        internal static SettingsContext Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"DeskMesh-switch-mode-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var options = AgentOptions.Parse(["--data-dir", directory]);
            var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
            return new SettingsContext(directory, options, identity);
        }

        public void Dispose()
        {
            var temporaryPath = SettingsPath + ".new";
            if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            System.IO.Directory.Delete(Directory, recursive: false);
        }
    }
}
