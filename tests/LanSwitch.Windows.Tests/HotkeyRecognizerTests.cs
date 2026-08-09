using LanSwitch.Windows.Input;

namespace LanSwitch.Windows.Tests;

public sealed class HotkeyRecognizerTests
{
    [Fact]
    public void DefaultPrimaryHotkeyTriggersOnlyOncePerPress()
    {
        var recognizer = new HotkeyRecognizer();

        Assert.Null(Press(recognizer, VirtualKeyCodes.LeftControl));
        Assert.Null(Press(recognizer, VirtualKeyCodes.LeftAlt));
        Assert.Equal(HotkeyCommand.SwitchToPrimary, Press(recognizer, VirtualKeyCodes.D1));
        Assert.Null(Press(recognizer, VirtualKeyCodes.D1));

        Release(recognizer, VirtualKeyCodes.D1);
        Assert.Equal(HotkeyCommand.SwitchToPrimary, Press(recognizer, VirtualKeyCodes.D1));
    }

    [Fact]
    public void EmergencyHotkeyHasASeparateThreeModifierChord()
    {
        var recognizer = new HotkeyRecognizer();

        _ = Press(recognizer, VirtualKeyCodes.Control);
        _ = Press(recognizer, VirtualKeyCodes.Alt);
        _ = Press(recognizer, VirtualKeyCodes.Shift);

        Assert.Equal(HotkeyCommand.EmergencyRelease, Press(recognizer, VirtualKeyCodes.F12));
    }

    [Fact]
    public void InjectedSignalsAreIgnoredByDefault()
    {
        var recognizer = new HotkeyRecognizer();

        _ = Press(recognizer, VirtualKeyCodes.Control);
        _ = Press(recognizer, VirtualKeyCodes.Alt);

        var command = recognizer.Process(new KeyboardSignal(
            VirtualKeyCodes.D2,
            InputTransition.Down,
            IsInjected: true));

        Assert.Null(command);
    }

    [Fact]
    public void ExtraModifierPreventsAnAccidentalSwitch()
    {
        var recognizer = new HotkeyRecognizer();

        _ = Press(recognizer, VirtualKeyCodes.Control);
        _ = Press(recognizer, VirtualKeyCodes.Alt);
        _ = Press(recognizer, VirtualKeyCodes.Shift);

        Assert.Null(Press(recognizer, VirtualKeyCodes.D2));
    }

    [Fact]
    public void BindingsCanBeReloadedWithoutRestartingTheHook()
    {
        var recognizer = new HotkeyRecognizer();
        recognizer.UpdateBindings(new HotkeyBindings(
            new HotkeyGesture(0x41, KeyModifiers.Control | KeyModifiers.Shift),
            new HotkeyGesture(0x42, KeyModifiers.Alt | KeyModifiers.Shift),
            new HotkeyGesture(0x1B, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)));

        Assert.Null(Press(recognizer, VirtualKeyCodes.Control));
        Assert.Null(Press(recognizer, VirtualKeyCodes.Shift));
        Assert.Equal(HotkeyCommand.SwitchToPrimary, Press(recognizer, 0x41));
        Assert.Equal(
            new HotkeyGesture(0x41, KeyModifiers.Control | KeyModifiers.Shift),
            recognizer.GetGesture(HotkeyCommand.SwitchToPrimary));
    }

    [Fact]
    public void ReloadingBindingsPreservesCurrentlyHeldModifiersForEmergencyRelease()
    {
        var recognizer = new HotkeyRecognizer();
        _ = Press(recognizer, VirtualKeyCodes.Control);
        _ = Press(recognizer, VirtualKeyCodes.Alt);
        _ = Press(recognizer, VirtualKeyCodes.Shift);

        recognizer.UpdateBindings(new HotkeyBindings(
            new HotkeyGesture(0x41, KeyModifiers.Control | KeyModifiers.Shift),
            new HotkeyGesture(0x42, KeyModifiers.Alt | KeyModifiers.Shift),
            new HotkeyGesture(0x1B, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)));

        Assert.Equal(HotkeyCommand.EmergencyRelease, Press(recognizer, 0x1B));
    }

    [Fact]
    public void RecognizedHotkeyMainKeyIsSuppressedThroughItsKeyUp()
    {
        var suppression = new WindowsLowLevelInputHook.HotkeySuppressionState();

        Assert.True(suppression.ShouldSuppress(0x57, InputTransition.Down, recognized: true));
        Assert.True(suppression.ShouldSuppress(0x57, InputTransition.Down, recognized: false));
        Assert.True(suppression.ShouldSuppress(0x57, InputTransition.Up, recognized: false));
        Assert.False(suppression.ShouldSuppress(0x57, InputTransition.Down, recognized: false));
        Assert.False(suppression.ShouldSuppress(0x58, InputTransition.Down, recognized: false));
    }

    private static HotkeyCommand? Press(HotkeyRecognizer recognizer, ushort key) =>
        recognizer.Process(new KeyboardSignal(key, InputTransition.Down));

    private static void Release(HotkeyRecognizer recognizer, ushort key) =>
        _ = recognizer.Process(new KeyboardSignal(key, InputTransition.Up));
}
