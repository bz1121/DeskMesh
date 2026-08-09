using LanSwitch.Agent.Infrastructure;
using System.Reflection;

namespace LanSwitch.Agent.Tests;

public sealed class DiagnosticLogTests
{
    [Fact]
    public async Task FocusReadAndWriteDoNotWaitForSlowStatusSnapshotWork()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LanSwitch-focus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = AgentOptions.Parse(["--data-dir", directory]);
            var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
            var settings = new SettingsStore(options, identity);
            var state = new AppState(identity, settings, options);
            var gate = typeof(AppState)
                .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(state)!;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = Task.Run(() =>
            {
                lock (gate)
                {
                    entered.SetResult();
                    release.Task.GetAwaiter().GetResult();
                }
            });

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                var next = new FocusView(7, "remote", "REMOTE", "remote", true);
                var operation = Task.Run(() =>
                {
                    state.SetFocus(next);
                    return state.Focus;
                });
                var result = await operation.WaitAsync(TimeSpan.FromMilliseconds(500));
                Assert.Same(next, result);
            }
            finally
            {
                release.SetResult();
                await holder.WaitAsync(TimeSpan.FromSeconds(2));
            }
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

    [Fact]
    public void DiagnosticRingIsNewestFirstBoundedAndClearable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LanSwitch-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = AgentOptions.Parse(["--data-dir", directory]);
            var identity = new DeviceIdentity(Guid.NewGuid().ToString("N"), null!, "LOCAL");
            var settings = new SettingsStore(options, identity);
            var state = new AppState(identity, settings, options);

            for (var index = 1; index <= 205; index++)
                state.AddDiagnostic("info", "test", $"message-{index}");

            Assert.Equal(200, state.Diagnostics.Count);
            Assert.Equal("message-205", state.Diagnostics[0].Message);
            Assert.Equal("message-6", state.Diagnostics[^1].Message);

            state.ClearDiagnostics();
            Assert.Empty(state.Diagnostics);
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
