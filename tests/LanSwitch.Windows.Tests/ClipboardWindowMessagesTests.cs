using LanSwitch.Windows.Clipboard;

namespace LanSwitch.Windows.Tests;

public sealed class ClipboardWindowMessagesTests
{
    [Fact]
    public void RecognizesOnlyClipboardUpdate()
    {
        Assert.True(ClipboardWindowMessages.IsClipboardUpdate(ClipboardWindowMessages.ClipboardUpdate));
        Assert.False(ClipboardWindowMessages.IsClipboardUpdate(0x000F));
    }
}
