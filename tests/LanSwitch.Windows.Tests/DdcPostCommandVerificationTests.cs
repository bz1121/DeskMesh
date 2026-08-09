using LanSwitch.Windows.Display;

namespace LanSwitch.Windows.Tests;

public sealed class DdcPostCommandVerificationTests
{
    [Fact]
    public async Task CancellationAfterACommandStopsVerificationWithoutThrowing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var shouldContinue = await DdcPostCommandVerification.WaitAsync(
            TimeSpan.FromMilliseconds(100),
            cancellation.Token);

        Assert.False(shouldContinue);
    }

    [Fact]
    public async Task CancellationIsObservedEvenWhenTheVerificationDelayIsZero()
    {
        var shouldContinue = await DdcPostCommandVerification.WaitAsync(
            TimeSpan.Zero,
            new CancellationToken(canceled: true));

        Assert.False(shouldContinue);
    }

    [Fact]
    public async Task AnUncancelledZeroDelayContinuesVerification()
    {
        var shouldContinue = await DdcPostCommandVerification.WaitAsync(
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.True(shouldContinue);
    }
}
