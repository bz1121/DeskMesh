namespace LanSwitch.Windows.Input;

public readonly record struct MouseMessageClassification(
    MouseButton? Button,
    InputTransition? Transition,
    bool IsWheel,
    bool IsHorizontalWheel);

public static class InputMessageClassifier
{
    public static InputTransition? ClassifyKeyboard(uint message) => message switch
    {
        0x0100 or 0x0104 => InputTransition.Down,
        0x0101 or 0x0105 => InputTransition.Up,
        _ => null
    };

    public static MouseMessageClassification ClassifyMouse(uint message, uint mouseData) => message switch
    {
        0x0201 => new MouseMessageClassification(MouseButton.Left, InputTransition.Down, false, false),
        0x0202 => new MouseMessageClassification(MouseButton.Left, InputTransition.Up, false, false),
        0x0204 => new MouseMessageClassification(MouseButton.Right, InputTransition.Down, false, false),
        0x0205 => new MouseMessageClassification(MouseButton.Right, InputTransition.Up, false, false),
        0x0207 => new MouseMessageClassification(MouseButton.Middle, InputTransition.Down, false, false),
        0x0208 => new MouseMessageClassification(MouseButton.Middle, InputTransition.Up, false, false),
        0x020B => new MouseMessageClassification(GetXButton(mouseData), InputTransition.Down, false, false),
        0x020C => new MouseMessageClassification(GetXButton(mouseData), InputTransition.Up, false, false),
        0x020A => new MouseMessageClassification(null, null, true, false),
        0x020E => new MouseMessageClassification(null, null, true, true),
        _ => default
    };

    public static short GetWheelDelta(uint mouseData) => unchecked((short)(mouseData >> 16));

    private static MouseButton? GetXButton(uint mouseData) => (mouseData >> 16) switch
    {
        1 => MouseButton.X1,
        2 => MouseButton.X2,
        _ => null
    };
}
