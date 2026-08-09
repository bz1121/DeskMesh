using LanSwitch.Windows.Display;

namespace LanSwitch.Windows.Tests;

public sealed class DdcSampleStabilityTests
{
    [Fact]
    public void IdenticalSamplesAreStable()
    {
        var first = new DdcVcpSample(0x11, 0x12, DdcVcpCodeType.SetParameter);

        Assert.True(DdcSampleStability.IsStable(first, first));
    }

    [Theory]
    [InlineData(0x0F, 0x11, 0x12, 0x12)]
    [InlineData(0x11, 0x11, 0x12, 0x13)]
    public void DifferentValuesAreUnstable(uint firstCurrent, uint secondCurrent, uint firstMax, uint secondMax)
    {
        var first = new DdcVcpSample(firstCurrent, firstMax, DdcVcpCodeType.SetParameter);
        var second = new DdcVcpSample(secondCurrent, secondMax, DdcVcpCodeType.SetParameter);

        Assert.False(DdcSampleStability.IsStable(first, second));
    }

    [Fact]
    public void DifferentCodeTypesAreUnstable()
    {
        var first = new DdcVcpSample(0x11, 0x12, DdcVcpCodeType.SetParameter);
        var second = new DdcVcpSample(0x11, 0x12, DdcVcpCodeType.Momentary);

        Assert.False(DdcSampleStability.IsStable(first, second));
    }
}
