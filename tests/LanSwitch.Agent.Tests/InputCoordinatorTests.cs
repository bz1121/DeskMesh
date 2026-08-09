using LanSwitch.Agent.Services;

namespace LanSwitch.Agent.Tests;

public sealed class InputCoordinatorTests
{
    [Fact]
    public async Task OutgoingBatchesUseMonotonicSequenceWithinEpoch()
    {
        var coordinator = new InputCoordinator();
        await coordinator.RouteToAsync(Peer(), 42, CancellationToken.None);

        Assert.True(coordinator.QueueLocalEvents([Event()]));
        Assert.True(coordinator.QueueLocalEvents([Event()]));
        Assert.True(coordinator.Outgoing.TryRead(out var first));
        Assert.True(coordinator.Outgoing.TryRead(out var second));
        Assert.Equal(42, first.Epoch);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
    }

    [Fact]
    public async Task MouseMoveBurstBeyondQueueCapacityIsCoalescedOrDroppedWithoutFailure()
    {
        var coordinator = new InputCoordinator();
        await coordinator.RouteToAsync(Peer(), 42, CancellationToken.None);

        for (var index = 0; index < 100_000; index++)
        {
            Assert.True(coordinator.QueueLocalEvents([MouseMove()]));
        }

        await Task.Delay(20);
        var expectedSequence = 0L;
        var queued = 0;
        while (coordinator.Outgoing.TryRead(out var batch))
        {
            Assert.Equal(++expectedSequence, batch.Sequence);
            var input = Assert.Single(batch.Events);
            Assert.Equal("mouse-move", input.Type);
            queued++;
        }

        // At most one retained aggregate may be published while this test drains the
        // bounded 1024-slot channel.
        Assert.InRange(queued, 1, 1025);
    }

    [Fact]
    public async Task FullQueueRetainsMouseMovementAndRejectsStateTransitionsUntilOrderingIsSafe()
    {
        var coordinator = new InputCoordinator();
        await coordinator.RouteToAsync(Peer(), 42, CancellationToken.None);
        for (var index = 0; index < 1024; index++)
        {
            Assert.True(coordinator.QueueLocalEvents([Event()]));
        }

        // Transient movement is accepted and retained instead of immediately forcing
        // a local return merely because a high-rate mouse filled the transport queue.
        Assert.True(coordinator.QueueLocalEvents([MouseMove()]));
        await Task.Delay(10);

        // Stateful input remains fail-safe while neither the saved movement nor the
        // transition itself can be queued.
        Assert.False(coordinator.QueueLocalEvents([Event()]));
        Assert.False(coordinator.QueueLocalEvents([MouseButton()]));

        // Even after one slot opens, that slot is used to restore the missing movement;
        // the click is rejected instead of being sent at the stale coordinate.
        Assert.True(coordinator.Outgoing.TryRead(out _));
        Assert.False(coordinator.QueueLocalEvents([MouseButton()]));

        // With another slot available, the click can now follow the retained movement.
        Assert.True(coordinator.Outgoing.TryRead(out _));
        Assert.True(coordinator.QueueLocalEvents([MouseButton()]));

        var tail = new List<InputBatchPacket>();
        while (coordinator.Outgoing.TryRead(out var packet)) tail.Add(packet);
        Assert.Equal("mouse-move", tail[^2].Events.Single().Type);
        Assert.Equal("mouse-button", tail[^1].Events.Single().Type);
        Assert.Equal(tail[^2].Sequence + 1, tail[^1].Sequence);
    }

    [Fact]
    public void OnlyPureMouseMovementIsEligibleForNonFatalQueueDropping()
    {
        Assert.True(WindowsIntegrationService.IsTransientMouseMoveOnly([MouseMove()]));
        Assert.False(WindowsIntegrationService.IsTransientMouseMoveOnly([Event()]));
        Assert.False(WindowsIntegrationService.IsTransientMouseMoveOnly([MouseButton()]));
        Assert.False(WindowsIntegrationService.IsTransientMouseMoveOnly([MouseMove(), MouseButton()]));
        Assert.False(WindowsIntegrationService.IsTransientMouseMoveOnly([]));
    }

    [Fact]
    public void DuplicateOrGapIsRejectedAndGapRevokesSession()
    {
        var coordinator = new InputCoordinator();
        var delivered = 0;
        var releases = 0;
        coordinator.RemoteBatchReceived += _ => delivered++;
        coordinator.ReleaseRequested += () => releases++;

        Assert.True(coordinator.PrepareIncoming(7, "peer"));
        Assert.True(coordinator.CommitIncoming(7, "peer"));
        Assert.True(coordinator.AcceptRemoteBatch("peer", Batch(7, 1)));
        Assert.False(coordinator.AcceptRemoteBatch("peer", Batch(7, 1)));
        Assert.False(coordinator.AcceptRemoteBatch("peer", Batch(7, 3)));
        Assert.False(coordinator.AcceptRemoteBatch("peer", Batch(7, 4)));
        Assert.Equal(1, delivered);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void RevokingOldEpochDoesNotRevokeNewCommittedEpoch()
    {
        var coordinator = new InputCoordinator();
        Assert.True(coordinator.PrepareIncoming(7, "peer"));
        Assert.True(coordinator.CommitIncoming(7, "peer"));
        Assert.True(coordinator.PrepareIncoming(8, "peer"));
        coordinator.RevokeIncoming("peer", 7);
        Assert.True(coordinator.CommitIncoming(8, "peer"));
        Assert.True(coordinator.AcceptRemoteBatch("peer", Batch(8, 1)));
        coordinator.RevokeIncoming("peer", 7);
        Assert.True(coordinator.AcceptRemoteBatch("peer", Batch(8, 2)));
    }

    [Fact]
    public void DisplaySwitchAuthorizationIsBoundToCurrentCommittedEpochAndConsumedOnce()
    {
        var coordinator = new InputCoordinator();
        Assert.True(coordinator.PrepareIncoming(7, "peer"));
        Assert.True(coordinator.CommitIncoming(7, "peer"));

        Assert.False(coordinator.TryBeginIncomingDisplaySwitch(6, "peer"));
        Assert.False(coordinator.TryBeginIncomingDisplaySwitch(7, "other-peer"));
        Assert.True(coordinator.TryBeginIncomingDisplaySwitch(7, "peer"));
        Assert.False(coordinator.TryBeginIncomingDisplaySwitch(7, "peer"));

        coordinator.CompleteIncomingDisplaySwitch(7, "peer");
        coordinator.RevokeIncoming("peer", 7);
        Assert.False(coordinator.TryBeginIncomingDisplaySwitch(7, "peer"));
    }

    [Fact]
    public void PreparedSessionCanCloseForDisplayRollbackAndBlocksNewFocusUntilCompletion()
    {
        var coordinator = new InputCoordinator();
        var releases = 0;
        coordinator.ReleaseRequested += () => releases++;
        Assert.True(coordinator.PrepareIncoming(7, "peer"));

        Assert.True(coordinator.TryBeginIncomingDisplaySwitch(7, "peer"));
        Assert.Equal(1, releases);
        Assert.False(coordinator.CommitIncoming(7, "peer"));
        Assert.False(coordinator.PrepareIncoming(8, "peer"));

        coordinator.CompleteIncomingDisplaySwitch(7, "peer");
        Assert.True(coordinator.PrepareIncoming(8, "peer"));
    }

    [Fact]
    public async Task DisplayClosingLinearizesAfterAnInFlightSynchronousInjection()
    {
        var coordinator = new InputCoordinator();
        var injectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowInjectionToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextOrder = 0;
        var injectionOrder = 0;
        var releaseOrder = 0;
        coordinator.RemoteBatchReceived += _ =>
        {
            injectionEntered.TrySetResult();
            allowInjectionToFinish.Task.GetAwaiter().GetResult();
            injectionOrder = Interlocked.Increment(ref nextOrder);
        };
        coordinator.ReleaseRequested += () => releaseOrder = Interlocked.Increment(ref nextOrder);
        Assert.True(coordinator.PrepareIncoming(7, "peer"));
        Assert.True(coordinator.CommitIncoming(7, "peer"));

        var batch = Task.Run(() => coordinator.AcceptRemoteBatch("peer", Batch(7, 1)));
        await injectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var closing = Task.Run(() => coordinator.TryBeginIncomingDisplaySwitch(7, "peer"));
        await Task.Delay(50);
        Assert.False(closing.IsCompleted);

        allowInjectionToFinish.TrySetResult();
        Assert.True(await batch.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, injectionOrder);
        Assert.Equal(2, releaseOrder);
    }

    [Fact]
    public void RecentlyClosedExactEpochCanAuthorizeReturnButOlderEpochCannotReplay()
    {
        var coordinator = new InputCoordinator();
        Assert.True(coordinator.PrepareIncoming(7, "peer"));
        Assert.True(coordinator.CommitIncoming(7, "peer"));
        coordinator.RevokeIncoming("peer", 7);

        Assert.True(coordinator.TryBeginIncomingDisplaySwitch(7, "peer"));
        coordinator.CompleteIncomingDisplaySwitch(7, "peer");
        Assert.False(coordinator.PrepareIncoming(7, "peer"));
        Assert.True(coordinator.PrepareIncoming(8, "peer"));
    }

    [Fact]
    public void DelayedEpochReleaseCannotClearANewerCommittedSession()
    {
        var coordinator = new InputCoordinator();
        Assert.True(coordinator.PrepareIncoming(7, "peer"));
        Assert.True(coordinator.CommitIncoming(7, "peer"));
        coordinator.ReleaseIncomingThrough("peer", 7);
        Assert.True(coordinator.PrepareIncoming(8, "peer"));
        Assert.True(coordinator.CommitIncoming(8, "peer"));

        coordinator.ReleaseIncomingThrough("peer", 7);

        Assert.True(coordinator.AcceptRemoteBatch("peer", Batch(8, 1)));
    }

    [Fact]
    public async Task EmergencyResetCancelsTheActiveOutgoingTransportSession()
    {
        var coordinator = new InputCoordinator();
        var initial = coordinator.GetOutgoingSession();
        await coordinator.RouteToAsync(Peer(), 9, CancellationToken.None);
        var routed = coordinator.GetOutgoingSession();

        Assert.True(initial.Token.IsCancellationRequested);
        Assert.True(routed.Generation > initial.Generation);
        Assert.NotNull(routed.Target);

        coordinator.EmergencyReset();
        var released = coordinator.GetOutgoingSession();
        Assert.True(routed.Token.IsCancellationRequested);
        Assert.True(released.Generation > routed.Generation);
        Assert.Null(released.Target);
    }

    [Fact]
    public async Task DisplayReturnStopsRoutingImmediatelyButKeepsTransportUntilHandoffCompletes()
    {
        var coordinator = new InputCoordinator();
        await coordinator.RouteToAsync(Peer(), 9, CancellationToken.None);

        var handoff = coordinator.BeginOutgoingDisplayReturn();
        var detached = coordinator.GetOutgoingSession();

        Assert.NotNull(handoff.Target);
        Assert.Null(detached.Target);
        Assert.False(handoff.Token.IsCancellationRequested);
        Assert.False(coordinator.QueueLocalEvents([Event()]));

        coordinator.CompleteOutgoingDisplayReturn(handoff.Generation);
        Assert.True(handoff.Token.IsCancellationRequested);
    }

    [Fact]
    public void InvalidEventRejectsWholeBatchBeforeAnyInputIsDelivered()
    {
        var coordinator = new InputCoordinator();
        var delivered = 0;
        var releases = 0;
        coordinator.RemoteBatchReceived += _ => delivered++;
        coordinator.ReleaseRequested += () => releases++;
        Assert.True(coordinator.PrepareIncoming(7, "peer"));
        Assert.True(coordinator.CommitIncoming(7, "peer"));

        var malformed = new InputBatchPacket(7,
            [Event(), new InputEventPacket("mouse-button", 999, 1, 0, 2)], 1);

        Assert.False(coordinator.AcceptRemoteBatch("peer", malformed));
        Assert.Equal(0, delivered);
        Assert.Equal(1, releases);
        Assert.False(coordinator.AcceptRemoteBatch("peer", Batch(7, 2)));
    }

    [Fact]
    public void InjectionCallbackFailureRevokesSessionAndRequestsRelease()
    {
        var coordinator = new InputCoordinator();
        var releases = 0;
        coordinator.ReleaseRequested += () => releases++;
        coordinator.RemoteBatchReceived += _ => throw new InvalidOperationException("模拟注入中途失败");
        Assert.True(coordinator.PrepareIncoming(7, "peer"));
        Assert.True(coordinator.CommitIncoming(7, "peer"));

        Assert.Throws<InvalidOperationException>(() => coordinator.AcceptRemoteBatch("peer", Batch(7, 1)));

        Assert.Equal(1, releases);
        Assert.False(coordinator.AcceptRemoteBatch("peer", Batch(7, 2)));
    }

    [Fact]
    public void DifferentPeerCannotReplaceACommittedInputSession()
    {
        var coordinator = new InputCoordinator();
        Assert.True(coordinator.PrepareIncoming(7, "peer-a"));
        Assert.True(coordinator.CommitIncoming(7, "peer-a"));
        Assert.True(coordinator.AcceptRemoteBatch("peer-a", Batch(7, 1)));

        Assert.False(coordinator.PrepareIncoming(8, "peer-b"));
        Assert.True(coordinator.AcceptRemoteBatch("peer-a", Batch(7, 2)));

        coordinator.RevokeIncoming("peer-a", 7);
        Assert.True(coordinator.PrepareIncoming(8, "peer-b"));
        Assert.True(coordinator.CommitIncoming(8, "peer-b"));
    }

    private static RuntimePeer Peer() => new("peer", "另一台电脑", "127.0.0.1", 45832, "AA", null,
        true, true, 1, DateTimeOffset.UtcNow, ["input"]);
    private static InputEventPacket Event() => new("keyboard", 65, 30, 0, 1);
    private static InputEventPacket MouseMove() => new("mouse-move", 1, -1, 0, 1);
    private static InputEventPacket MouseButton() => new("mouse-button", 0, 1, 0, 1);
    private static InputBatchPacket Batch(long epoch, long sequence) => new(epoch, [Event()], sequence);
}
