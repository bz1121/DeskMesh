using LanSwitch.Agent.Services;
using LanSwitch.Agent.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace LanSwitch.Agent.Tests;

public sealed class DistributedPhysicalFollowTests
{
    [Fact]
    public void ManualFocusChangeClearsOldSamplesAndCreatesAQuietWindow()
    {
        var suppressor = new FocusTransitionObservationGate(TimeSpan.FromSeconds(3));
        var start = Stopwatch.GetTimestamp();
        var twoSeconds = checked((long)(Stopwatch.Frequency * 2d));
        var fourSeconds = checked((long)(Stopwatch.Frequency * 4d));

        Assert.False(suppressor.ObserveFocus("local", start, out var initialized));
        Assert.False(initialized);
        Assert.True(suppressor.ObserveFocus("remote", start + 1, out var changed));
        Assert.True(changed);
        Assert.True(suppressor.ObserveFocus("remote", start + 1 + twoSeconds, out changed));
        Assert.False(changed);
        Assert.False(suppressor.ObserveFocus("remote", start + 1 + fourSeconds, out changed));
        Assert.False(changed);
    }

    [Fact]
    public void StaleOldInputCannotReclaimFocusUntilDepartureIsProven()
    {
        var gate = new FocusTransitionObservationGate(TimeSpan.FromSeconds(3));
        var start = Stopwatch.GetTimestamp();
        var afterQuiet = start + checked((long)(Stopwatch.Frequency * 4d));

        Assert.False(gate.ObserveFocus("local", start, out _));
        Assert.True(gate.ObserveFocus("remote", start + 1, out _));

        Assert.False(gate.AcceptObservation("local", afterQuiet, out _));
        Assert.False(gate.AcceptObservation("local", afterQuiet + 1, out _));
        Assert.False(gate.AcceptObservation(null, afterQuiet + 2, out var armed));
        Assert.False(armed);
        Assert.False(gate.AcceptObservation(null, afterQuiet + 3, out armed));
        Assert.True(armed);
        Assert.True(gate.AcceptObservation("local", afterQuiet + 4, out _));
    }

    [Fact]
    public void StableObservationRequiresTwoConsecutiveMatchingSamples()
    {
        var tracker = new StableDisplayObservationTracker();

        Assert.False(tracker.Observe("desktop", out _));
        Assert.True(tracker.Observe("desktop", out var stable));
        Assert.Equal("desktop", stable);
    }

    [Fact]
    public void MissingOrChangedSampleRestartsStabilityWindow()
    {
        var tracker = new StableDisplayObservationTracker();

        Assert.False(tracker.Observe("desktop", out _));
        Assert.False(tracker.Observe(null, out _));
        Assert.False(tracker.Observe("desktop", out _));
        Assert.False(tracker.Observe("laptop", out _));
        Assert.True(tracker.Observe("laptop", out var stable));
        Assert.Equal("laptop", stable);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("ABCDEF", false)]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG", false)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    public void SubscriptionSessionIdHasFixedCryptographicShape(string? value, bool expected)
    {
        Assert.Equal(expected, PhysicalFollowProtocol.IsValidSessionId(value));
    }

    [Fact]
    public void ObservationRejectsReplayWrongPeerAndWrongSession()
    {
        const string session = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        Assert.True(PhysicalFollowProtocol.IsObservationAccepted(
            session, 4, "desktop",
            new PhysicalFollowObservation(session, 5, "desktop")));
        Assert.False(PhysicalFollowProtocol.IsObservationAccepted(
            session, 5, "desktop",
            new PhysicalFollowObservation(session, 5, "desktop")));
        Assert.False(PhysicalFollowProtocol.IsObservationAccepted(
            session, 4, "desktop",
            new PhysicalFollowObservation(session, 6, "laptop")));
        Assert.False(PhysicalFollowProtocol.IsObservationAccepted(
            session, 4, "desktop",
            new PhysicalFollowObservation(new string('A', 48), 6, "desktop")));
    }

    [Fact]
    public void SameSessionRenewalPreservesPublisherSequenceAndThrottleProgress()
    {
        var original = new InboundFollowSession(
            new string('A', 48), "desktop", new string('B', 64),
            10, TimeSpan.FromSeconds(8), 7, 20,
            CancellationToken.None, default);

        var refreshed = DistributedPhysicalFollowService.RefreshInboundSession(
            original, renewedTimestamp: 30, TimeSpan.FromSeconds(9));

        Assert.Equal(30, refreshed.LastRenewedTimestamp);
        Assert.Equal(TimeSpan.FromSeconds(9), refreshed.Lease);
        Assert.Equal(7, refreshed.LastSequence);
        Assert.Equal(20, refreshed.LastPublishedTimestamp);
    }

    [Fact]
    public void PublishAttemptAdvancesSequenceBeforeNetworkAcknowledgement()
    {
        var original = new InboundFollowSession(
            new string('A', 48), "desktop", new string('B', 64),
            10, TimeSpan.FromSeconds(8), 7, 20,
            CancellationToken.None, default);

        var advanced = DistributedPhysicalFollowService.AdvanceInboundPublish(original, 30);

        Assert.Equal(8, advanced.LastSequence);
        Assert.Equal(30, advanced.LastPublishedTimestamp);
    }

    [Fact]
    public void ReenabledSubscriberSessionRejectsAnObservationFromTheOldSession()
    {
        var current = new OutboundFollowSession(
            new string('B', 48), "desktop", new string('C', 64),
            CancellationToken.None, 10, 10, false, 4);
        var stale = new PhysicalFollowObservation(new string('A', 48), 4, "desktop");
        var exact = new PhysicalFollowObservation(new string('B', 48), 4, "desktop");

        Assert.False(DistributedPhysicalFollowService.IsObservationTransitionCurrent(current, stale));
        Assert.True(DistributedPhysicalFollowService.IsObservationTransitionCurrent(current, exact));
    }

    [Fact]
    public async Task TrustRevocationImmediatelyRemovesPublisherSubscription()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LanSwitch-follow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = AgentOptions.Parse(["--data-dir", directory]);
            var identity = new DeviceIdentity("local", null!, "LOCAL");
            var settings = new SettingsStore(options, identity);
            var state = new AppState(identity, settings, options);
            var peers = new PeerDirectory(settings);
            var display = new DisplayCoordinator(settings, state);
            display.ProbeRequested += _ => Task.FromResult<IReadOnlyList<DisplayProbeView>>([]);
            display.SwitchRequested += (_, _, _) => Task.FromResult(
                new DisplayOperationResult(true, true, true, "test", CommandIssued: true));
            var input = new InputCoordinator();
            var focus = new FocusCoordinator(identity, settings, peers, new PeerHttpClientFactory(identity),
                state, input, display, new RemoteDesktopOutgoingSessionRegistry());
            var service = new DistributedPhysicalFollowService(identity, settings, peers,
                new PeerHttpClientFactory(identity), display, focus, state,
                NullLogger<DistributedPhysicalFollowService>.Instance);
            var peer = new RuntimePeer("desktop", "Desktop", "192.168.1.2", 45832,
                new string('A', 64), null, true, true, 1, DateTimeOffset.UtcNow, ["display"]);
            await settings.UpdateAsync(current => current with
            {
                DisplayMappings =
                [
                    new StoredDisplayMapping("monitor", identity.DeviceId, "DP", 7),
                    new StoredDisplayMapping("monitor", peer.Id, "HDMI", 5)
                ]
            });
            using var trust = new CancellationTokenSource();
            var command = new PhysicalFollowSubscriptionCommand(
                "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", peer.Id, identity.DeviceId, 8_000);

            Assert.True(service.AcceptSubscription(peer, command, trust.Token).Accepted);
            Assert.Equal(1, service.InboundSubscriptionCount);

            trust.Cancel();

            Assert.Equal(0, service.InboundSubscriptionCount);
            Assert.False(service.AcceptSubscription(peer, command, trust.Token).Accepted);
            Assert.Equal(0, service.InboundSubscriptionCount);
        }
        finally
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var temporaryPath = settingsPath + ".new";
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            Directory.Delete(directory, recursive: false);
        }
    }
}
