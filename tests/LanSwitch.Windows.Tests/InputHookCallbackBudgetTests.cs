using LanSwitch.Windows.Input;

namespace LanSwitch.Windows.Tests;

public sealed class InputHookCallbackBudgetTests
{
    [Fact]
    public void DefaultBudgetStaysBelowTheWindowsMaximumTimeout()
    {
        var options = new InputHookOptions();

        Assert.InRange(
            options.MaximumCallbackDuration,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(750));
    }

    [Fact]
    public void ElapsedTimeMustExceedTheBudgetToFailOpen()
    {
        var budget = TimeSpan.FromMilliseconds(250);

        Assert.False(InputHookCallbackBudget.IsExceeded(budget, budget));
        Assert.True(InputHookCallbackBudget.IsExceeded(TimeSpan.FromMilliseconds(251), budget));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(751)]
    public void InvalidCallbackBudgetsAreRejectedBeforeHooksStart(int milliseconds)
    {
        var options = new InputHookOptions
        {
            MaximumCallbackDuration = TimeSpan.FromMilliseconds(milliseconds)
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsLowLevelInputHook(options));
    }
}
