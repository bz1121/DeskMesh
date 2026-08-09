using LanSwitch.Core.Protocol;

namespace LanSwitch.Core.Tests;

public sealed class ProtocolValidatorTests
{
    [Fact]
    public void IncreasingSequence_IsAccepted_WhileReplayIsRejected()
    {
        var validator = new ProtocolValidator();

        Assert.True(validator.ValidateAndTrack(CreateEnvelope(epoch: 0, sequence: 1)).IsValid);
        Assert.True(validator.ValidateAndTrack(CreateEnvelope(epoch: 0, sequence: 2)).IsValid);

        var replay = validator.ValidateAndTrack(CreateEnvelope(epoch: 0, sequence: 2));

        Assert.False(replay.IsValid);
        Assert.Equal(ProtocolValidationError.ReplayOrOutOfOrder, replay.Error);
    }

    [Fact]
    public void NewEpoch_MustRestartAtOne_AndRejectsOldEpoch()
    {
        var validator = new ProtocolValidator();
        Assert.True(validator.ValidateAndTrack(CreateEnvelope(epoch: 0, sequence: 1)).IsValid);

        var invalidStart = validator.ValidateAndTrack(CreateEnvelope(epoch: 2, sequence: 7));
        Assert.Equal(ProtocolValidationError.FirstSequenceMustBeOne, invalidStart.Error);

        Assert.True(validator.ValidateAndTrack(CreateEnvelope(epoch: 2, sequence: 1)).IsValid);

        var stale = validator.ValidateAndTrack(CreateEnvelope(epoch: 1, sequence: 2));
        Assert.Equal(ProtocolValidationError.StaleEpoch, stale.Error);
    }

    [Fact]
    public void UnsupportedVersion_IsRejectedBeforeTracking()
    {
        var validator = new ProtocolValidator();
        var envelope = CreateEnvelope(epoch: 0, sequence: 1) with
        {
            Version = ProtocolConstants.CurrentVersion + 1
        };

        var result = validator.ValidateAndTrack(envelope);

        Assert.Equal(ProtocolValidationError.UnsupportedVersion, result.Error);
        Assert.False(validator.TryGetPosition("peer-a", out _));
    }

    [Fact]
    public void InputMessage_RequiresActiveEpoch()
    {
        var result = ProtocolValidator.ValidateEnvelope(CreateEnvelope(epoch: 0, sequence: 1) with
        {
            Type = ProtocolMessageType.InputBatch
        });

        Assert.Equal(ProtocolValidationError.ActiveEpochRequired, result.Error);
    }

    private static ProtocolEnvelope CreateEnvelope(long epoch, long sequence)
    {
        return new ProtocolEnvelope
        {
            Type = ProtocolMessageType.Heartbeat,
            SenderDeviceId = "peer-a",
            Epoch = epoch,
            Sequence = sequence,
            SentAtUtc = DateTimeOffset.UtcNow
        };
    }
}
