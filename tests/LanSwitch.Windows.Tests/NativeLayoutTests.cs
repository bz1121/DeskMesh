using System.Runtime.InteropServices;
using LanSwitch.Windows.Interop;

namespace LanSwitch.Windows.Tests;

public sealed class NativeLayoutTests
{
    [Fact]
    public void DisplayDeviceWLayoutMatchesTheWin32Abi()
    {
        Assert.Equal(840, Marshal.SizeOf<NativeMethods.DisplayDevice>());
    }
}
