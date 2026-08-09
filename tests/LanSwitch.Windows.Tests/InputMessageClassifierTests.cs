using LanSwitch.Windows.Input;

namespace LanSwitch.Windows.Tests;

public sealed class InputMessageClassifierTests
{
    [Theory]
    [InlineData(0x0100, InputTransition.Down)]
    [InlineData(0x0104, InputTransition.Down)]
    [InlineData(0x0101, InputTransition.Up)]
    [InlineData(0x0105, InputTransition.Up)]
    public void KeyboardMessagesAreClassified(uint message, InputTransition expected)
    {
        Assert.Equal(expected, InputMessageClassifier.ClassifyKeyboard(message));
    }

    [Fact]
    public void XButtonAndWheelDataAreDecoded()
    {
        var xButton = InputMessageClassifier.ClassifyMouse(0x020B, 2u << 16);

        Assert.Equal(MouseButton.X2, xButton.Button);
        Assert.Equal(InputTransition.Down, xButton.Transition);
        Assert.Equal(-120, InputMessageClassifier.GetWheelDelta(unchecked((uint)(-120 << 16))));
    }
}
