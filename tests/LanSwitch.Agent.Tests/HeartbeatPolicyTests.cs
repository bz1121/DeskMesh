using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Tests;

public sealed class HeartbeatPolicyTests
{
    [Fact]
    public void RequestTimeoutToleratesBriefLanJitterButStaysWithinFailLocalTarget()
    {
        Assert.InRange(
            HeartbeatService.RequestTimeout,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1.5));
    }
}
