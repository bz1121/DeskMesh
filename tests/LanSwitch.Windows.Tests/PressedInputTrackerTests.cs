using LanSwitch.Windows.Input;

namespace LanSwitch.Windows.Tests;

public sealed class PressedInputTrackerTests
{
    [Fact]
    public void ReleasePlanIsReversePressOrderAndContainsNoDuplicates()
    {
        var tracker = new PressedInputTracker();
        var keyDown = new KeyboardInjection(0x41, 0x1E, InputTransition.Down);
        var buttonDown = new MouseButtonInjection(MouseButton.Left, InputTransition.Down);

        tracker.RecordKeyboard(keyDown);
        tracker.RecordKeyboard(keyDown);
        tracker.RecordMouseButton(buttonDown);

        var plan = tracker.CreateReleasePlan();

        Assert.Equal(2, plan.Count);
        Assert.Equal(TrackedInputKind.MouseButton, plan[0].Kind);
        Assert.Equal(TrackedInputKind.Keyboard, plan[1].Kind);
    }

    [Fact]
    public void NormalUpAndConfirmedEmergencyReleaseRemoveTrackedInputs()
    {
        var tracker = new PressedInputTracker();
        tracker.RecordKeyboard(new KeyboardInjection(0x41, 0x1E, InputTransition.Down));
        tracker.RecordMouseButton(new MouseButtonInjection(MouseButton.Right, InputTransition.Down));

        tracker.RecordKeyboard(new KeyboardInjection(0x41, 0x1E, InputTransition.Up));
        var release = Assert.Single(tracker.CreateReleasePlan());

        Assert.True(tracker.ConfirmReleased(release));
        Assert.Equal(0, tracker.Count);
    }
}
