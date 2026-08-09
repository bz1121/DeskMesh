using LanSwitch.Windows.Input;
using LanSwitch.Windows.Interop;
using System.Runtime.InteropServices;

namespace LanSwitch.Windows.Tests;

public sealed class RawMouseInputTests
{
    [Fact]
    public void NativeMouseLayoutMatchesWin32Abi()
    {
        var expectedHeaderSize = IntPtr.Size == 8 ? 24 : 16;
        Assert.Equal(expectedHeaderSize, Marshal.SizeOf<NativeMethods.RawInputHeader>());
        Assert.Equal(24, Marshal.SizeOf<NativeMethods.RawMouse>());
        Assert.Equal(expectedHeaderSize + 24, Marshal.SizeOf<NativeMethods.RawInput>());
    }

    [Fact]
    public void MonitorRegistersAndUnregistersRawMouseInput()
    {
        using var monitor = new WindowsRawMouseInputMonitor();

        monitor.Start();
        Assert.True(monitor.IsRunning);

        monitor.Stop();
        Assert.False(monitor.IsRunning);
    }

    [Theory]
    [InlineData(4, -7)]
    [InlineData(-120, 80)]
    [InlineData(1, 0)]
    public void RelativeMovementPreservesHardwareDelta(int x, int y)
    {
        Assert.True(WindowsRawMouseInputMonitor.TryGetRelativeDelta(0, x, y, out var actualX, out var actualY));
        Assert.Equal(x, actualX);
        Assert.Equal(y, actualY);
    }

    [Fact]
    public void EmptyMovementIsIgnored()
    {
        Assert.False(WindowsRawMouseInputMonitor.TryGetRelativeDelta(0, 0, 0, out _, out _));
    }

    [Fact]
    public void AbsoluteDevicesAreIgnoredInsteadOfBeingMisreadAsHugeRelativeMovement()
    {
        Assert.False(WindowsRawMouseInputMonitor.TryGetRelativeDelta(1, 32767, 32767, out _, out _));
    }
}
