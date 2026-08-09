using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Tests;

public sealed class FocusSwitchCancellationTests
{
    [Fact]
    public void EmergencyBetweenGenerationCaptureAndRegistrationRejectsTheOldSwitch()
    {
        var cancellation = new FocusSwitchCancellation();
        var generation = cancellation.CaptureGeneration();

        cancellation.CancelAll();
        using var operation = new CancellationTokenSource();

        Assert.False(cancellation.TryRegister(generation, operation));
        Assert.False(operation.IsCancellationRequested);
    }

    [Fact]
    public void EmergencyAfterRegistrationCancelsTheActiveSwitch()
    {
        var cancellation = new FocusSwitchCancellation();
        using var operation = new CancellationTokenSource();
        Assert.True(cancellation.TryRegister(cancellation.CaptureGeneration(), operation));

        cancellation.CancelAll();

        Assert.True(operation.IsCancellationRequested);
        cancellation.Complete(operation);
    }
}
