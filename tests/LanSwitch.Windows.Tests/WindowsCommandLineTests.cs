using LanSwitch.Windows.Startup;

namespace LanSwitch.Windows.Tests;

public sealed class WindowsCommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void ArgumentsUseCommandLineToArgvWCompatibleQuoting(string input, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.QuoteArgument(input));
    }

    [Fact]
    public void TrailingBackslashesAreEscapedBeforeClosingQuote()
    {
        var quoted = WindowsCommandLine.QuoteArgument(@"C:\Program Files\LanSwitch\");

        Assert.Equal("\"C:\\Program Files\\LanSwitch\\\\\"", quoted);
    }

    [Fact]
    public void BuildQuotesExecutableAndEachArgumentIndependently()
    {
        var command = WindowsCommandLine.Build(
            @"C:\Program Files\LanSwitch\LanSwitch.Agent.exe",
            ["--profile", "Home Office"]);

        Assert.Equal(
            "\"C:\\Program Files\\LanSwitch\\LanSwitch.Agent.exe\" --profile \"Home Office\"",
            command);
    }
}
