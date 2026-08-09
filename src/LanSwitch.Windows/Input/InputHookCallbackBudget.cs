namespace LanSwitch.Windows.Input;

internal static class InputHookCallbackBudget
{
    internal static void Validate(TimeSpan maximumDuration)
    {
        if (maximumDuration <= TimeSpan.Zero || maximumDuration > TimeSpan.FromMilliseconds(750))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDuration),
                "The low-level hook callback budget must be greater than zero and at most 750 milliseconds.");
        }
    }

    internal static bool IsExceeded(TimeSpan elapsed, TimeSpan maximumDuration)
    {
        Validate(maximumDuration);
        return elapsed > maximumDuration;
    }
}
