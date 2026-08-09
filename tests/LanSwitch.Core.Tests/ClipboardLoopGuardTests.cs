using System.Text;
using LanSwitch.Core.Clipboard;

namespace LanSwitch.Core.Tests;

public sealed class ClipboardLoopGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecentlyAppliedRemoteDigest_IsSuppressedUntilTtlExpires()
    {
        var guard = new ClipboardLoopGuard(fallbackTtl: TimeSpan.FromSeconds(5));
        var digest = ClipboardLoopGuard.ComputeDigest("text/plain", Encoding.UTF8.GetBytes("hello"));
        var origin = new ClipboardOrigin("peer", 4, digest);

        guard.RememberRemoteApplication(origin, Start);

        Assert.True(guard.ShouldSuppress(digest, marker: null, Start.AddSeconds(4)));
        Assert.False(guard.ShouldSuppress(digest, marker: null, Start.AddSeconds(5)));
    }

    [Fact]
    public void MatchingOriginMarker_SuppressesEvenWithoutCache()
    {
        var guard = new ClipboardLoopGuard();
        var digest = ClipboardLoopGuard.ComputeDigest("text/plain", Encoding.UTF8.GetBytes("hello"));
        var marker = new ClipboardOrigin("peer", 10, digest);

        Assert.True(guard.ShouldSuppress(digest, marker, Start));
    }

    [Fact]
    public void DigestIncludesFormatAndContent()
    {
        var bytes = Encoding.UTF8.GetBytes("same");
        var text = ClipboardLoopGuard.ComputeDigest("text/plain", bytes);
        var image = ClipboardLoopGuard.ComputeDigest("image/png", bytes);
        var otherText = ClipboardLoopGuard.ComputeDigest("text/plain", Encoding.UTF8.GetBytes("other"));

        Assert.NotEqual(text, image);
        Assert.NotEqual(text, otherText);
    }
}
