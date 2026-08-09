using System.Drawing;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Tests;

public sealed class RemoteDesktopTests
{
    [Fact]
    public void LargeFramesAreScaledWithoutChangingAspectRatio()
    {
        Assert.Equal(new Size(1920, 1080), RemoteDesktopProtocol.ScaleToFit(new Size(3840, 2160)));
        Assert.Equal(new Size(1280, 1024), RemoteDesktopProtocol.ScaleToFit(new Size(1280, 1024)));
        Assert.Throws<InvalidOperationException>(() =>
            RemoteDesktopProtocol.ScaleToFit(new Size(10000, 10000)));
    }

    [Fact]
    public void PointerOnSecondMonitorMapsAcrossVirtualDesktop()
    {
        var virtualDesktop = new Rectangle(0, 0, 3840, 1080);
        var secondMonitor = new Rectangle(1920, 0, 1920, 1080);

        var topLeft = RemoteDesktopPointerMapper.MapToNormalizedVirtualDesktop(
            0,
            0,
            secondMonitor,
            virtualDesktop);
        var bottomRight = RemoteDesktopPointerMapper.MapToNormalizedVirtualDesktop(
            1,
            1,
            secondMonitor,
            virtualDesktop);

        Assert.InRange(topLeft.X, 32760, 32780);
        Assert.Equal(0, topLeft.Y);
        Assert.Equal(65535, bottomRight.X);
        Assert.Equal(65535, bottomRight.Y);
    }

    [Fact]
    public void PointerSupportsMonitorLeftOfPrimary()
    {
        var virtualDesktop = new Rectangle(-1280, 0, 3200, 1080);
        var leftMonitor = new Rectangle(-1280, 0, 1280, 1024);

        var left = RemoteDesktopPointerMapper.MapToNormalizedVirtualDesktop(
            0,
            0.5,
            leftMonitor,
            virtualDesktop);
        var right = RemoteDesktopPointerMapper.MapToNormalizedVirtualDesktop(
            1,
            0.5,
            leftMonitor,
            virtualDesktop);

        Assert.Equal(0, left.X);
        Assert.InRange(right.X, 26190, 26220);
        Assert.InRange(left.Y, 31070, 31120);
    }

    [Fact]
    public void InvalidPointerCoordinatesAreRejected()
    {
        var bounds = new Rectangle(0, 0, 1920, 1080);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemoteDesktopPointerMapper.MapToNormalizedVirtualDesktop(double.NaN, 0, bounds, bounds));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemoteDesktopPointerMapper.MapToNormalizedVirtualDesktop(1.1, 0, bounds, bounds));
    }

    [Fact]
    public void RemoteDesktopSettingsAreDisabledAndBoundedByDefault()
    {
        var defaults = AgentSettings.CreateDefault("local");

        Assert.False(defaults.RemoteDesktopEnabled);
        Assert.Equal(30, defaults.RemoteDesktopFramesPerSecond);
        Assert.Equal(60, defaults.RemoteDesktopJpegQuality);

        var normalized = RemoteDesktopConfiguration.Normalize(defaults with
        {
            RemoteDesktopFramesPerSecond = 100,
            RemoteDesktopJpegQuality = -1
        });
        Assert.Equal(RemoteDesktopConfiguration.MaximumFramesPerSecond, normalized.RemoteDesktopFramesPerSecond);
        Assert.Equal(RemoteDesktopConfiguration.MinimumJpegQuality, normalized.RemoteDesktopJpegQuality);
        RemoteDesktopConfiguration.Validate(90, 60);
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteDesktopConfiguration.Validate(91, 60));
    }

    [Fact]
    public void RemoteDesktopSessionIdsAreStrictUppercaseHex()
    {
        Assert.True(RemoteDesktopProtocol.IsValidSessionId("0123456789ABCDEF0123456789ABCDEF"));
        Assert.False(RemoteDesktopProtocol.IsValidSessionId("0123456789abcdef0123456789abcdef"));
        Assert.False(RemoteDesktopProtocol.IsValidSessionId("0123456789ABCDEF"));
        Assert.False(RemoteDesktopProtocol.IsValidSessionId(null));
    }

    [Fact]
    public void RemoteDesktopSessionRegistryRevokesDisposedSessions()
    {
        var registry = new RemoteDesktopSessionRegistry();
        const string sessionId = "0123456789ABCDEF0123456789ABCDEF";

        using (registry.Activate("peer-a", sessionId))
        {
            Assert.True(registry.IsActive("peer-a", sessionId));
            Assert.False(registry.IsActive("peer-b", sessionId));
            Assert.Throws<InvalidOperationException>(() => registry.Activate("peer-a", sessionId));
        }

        Assert.False(registry.IsActive("peer-a", sessionId));
    }
}
