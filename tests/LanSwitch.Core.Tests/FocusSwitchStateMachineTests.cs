using LanSwitch.Core.Focus;
using LanSwitch.Core.Models;

namespace LanSwitch.Core.Tests;

public sealed class FocusSwitchStateMachineTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadyAndCommit_MoveFocusToRemote()
    {
        var machine = new FocusSwitchStateMachine("local", Start);

        var preparing = machine.BeginRemoteSwitch("peer", Start);
        var ready = machine.MarkTargetReady(preparing.Epoch, Start.AddMilliseconds(20));
        var remote = machine.CommitRemote(preparing.Epoch, Start.AddMilliseconds(30));

        Assert.Equal(FocusState.PreparingRemote, preparing.State);
        Assert.True(ready.TargetReady);
        Assert.Equal(FocusState.Remote, remote.State);
        Assert.Equal("peer", remote.TargetDeviceId);
    }

    [Fact]
    public void MissingHeartbeat_FallsBackAtTwoSecondsAndReleasesInputs()
    {
        var machine = new FocusSwitchStateMachine("local", Start);
        var epoch = machine.BeginRemoteSwitch("peer", Start).Epoch;
        machine.MarkTargetReady(epoch, Start);
        machine.CommitRemote(epoch, Start);

        Assert.Null(machine.FailBackIfTimedOut(Start.AddMilliseconds(1999)));

        var fallback = machine.FailBackIfTimedOut(Start.AddSeconds(2));

        Assert.NotNull(fallback);
        Assert.Equal(FocusFallbackReason.HeartbeatTimeout, fallback.Reason);
        Assert.True(fallback.ReleaseAllInputs);
        Assert.True(fallback.AttemptDisplayRevert);
        Assert.Equal(FocusState.Local, machine.Snapshot.State);
        Assert.Null(machine.Snapshot.TargetDeviceId);
    }

    [Fact]
    public void PreparingTarget_AlsoFallsBackAfterTimeout()
    {
        var machine = new FocusSwitchStateMachine("local", Start);
        machine.BeginRemoteSwitch("peer", Start);

        var fallback = machine.FailBackIfTimedOut(Start.AddSeconds(2));

        Assert.NotNull(fallback);
        Assert.Equal(FocusFallbackReason.PrepareTimeout, fallback.Reason);
        Assert.Equal(FocusState.Local, machine.Snapshot.State);
    }

    [Fact]
    public void StaleEpoch_CannotReadyNewSwitch()
    {
        var machine = new FocusSwitchStateMachine("local", Start);
        var currentEpoch = machine.BeginRemoteSwitch("peer", Start).Epoch;

        Assert.Throws<InvalidOperationException>(() =>
            machine.MarkTargetReady(currentEpoch - 1, Start.AddMilliseconds(1)));
    }
}
