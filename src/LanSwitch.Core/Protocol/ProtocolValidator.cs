using System.Collections.Concurrent;

namespace LanSwitch.Core.Protocol;

public sealed class ProtocolValidator
{
    private static readonly HashSet<ProtocolMessageType> ActiveEpochMessages =
    [
        ProtocolMessageType.FocusPrepare,
        ProtocolMessageType.FocusReady,
        ProtocolMessageType.FocusCommit,
        ProtocolMessageType.FocusAbort,
        ProtocolMessageType.InputBatch,
        ProtocolMessageType.ReleaseAll,
        ProtocolMessageType.DisplaySwitch,
        ProtocolMessageType.DisplayResult
    ];

    private readonly ConcurrentDictionary<string, SequenceState> _peerPositions =
        new(StringComparer.Ordinal);

    public ProtocolValidationResult ValidateAndTrack(ProtocolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var statelessResult = ValidateEnvelope(envelope);
        if (!statelessResult.IsValid)
        {
            return statelessResult;
        }

        var state = _peerPositions.GetOrAdd(envelope.SenderDeviceId, static _ => new SequenceState());
        lock (state.SyncRoot)
        {
            if (!state.Initialized)
            {
                if (envelope.Sequence != 1)
                {
                    return ProtocolValidationResult.Failure(
                        ProtocolValidationError.FirstSequenceMustBeOne,
                        "The first message from a peer must use sequence 1.");
                }

                state.Initialized = true;
                state.Epoch = envelope.Epoch;
                state.HighestSequence = envelope.Sequence;
                return ProtocolValidationResult.Success;
            }

            if (envelope.Epoch < state.Epoch)
            {
                return ProtocolValidationResult.Failure(
                    ProtocolValidationError.StaleEpoch,
                    $"Epoch {envelope.Epoch} is older than the accepted epoch {state.Epoch}.");
            }

            if (envelope.Epoch > state.Epoch)
            {
                if (envelope.Sequence != 1)
                {
                    return ProtocolValidationResult.Failure(
                        ProtocolValidationError.FirstSequenceMustBeOne,
                        "The first message in a new epoch must use sequence 1.");
                }

                state.Epoch = envelope.Epoch;
                state.HighestSequence = envelope.Sequence;
                return ProtocolValidationResult.Success;
            }

            if (envelope.Sequence <= state.HighestSequence)
            {
                return ProtocolValidationResult.Failure(
                    ProtocolValidationError.ReplayOrOutOfOrder,
                    $"Sequence {envelope.Sequence} has already been accepted or arrived out of order.");
            }

            state.HighestSequence = envelope.Sequence;
            return ProtocolValidationResult.Success;
        }
    }

    public static ProtocolValidationResult ValidateEnvelope(ProtocolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Version is < ProtocolConstants.MinimumSupportedVersion or > ProtocolConstants.CurrentVersion)
        {
            return ProtocolValidationResult.Failure(
                ProtocolValidationError.UnsupportedVersion,
                $"Protocol version {envelope.Version} is not supported.");
        }

        if (envelope.MessageId == Guid.Empty)
        {
            return ProtocolValidationResult.Failure(
                ProtocolValidationError.MissingMessageId,
                "MessageId must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(envelope.SenderDeviceId))
        {
            return ProtocolValidationResult.Failure(
                ProtocolValidationError.MissingSenderDeviceId,
                "SenderDeviceId is required.");
        }

        if (envelope.Epoch < 0)
        {
            return ProtocolValidationResult.Failure(
                ProtocolValidationError.InvalidEpoch,
                "Epoch must not be negative.");
        }

        if (ActiveEpochMessages.Contains(envelope.Type) && envelope.Epoch == 0)
        {
            return ProtocolValidationResult.Failure(
                ProtocolValidationError.ActiveEpochRequired,
                $"{envelope.Type} requires an active epoch greater than zero.");
        }

        if (envelope.Sequence <= 0)
        {
            return ProtocolValidationResult.Failure(
                ProtocolValidationError.InvalidSequence,
                "Sequence must be greater than zero.");
        }

        return ProtocolValidationResult.Success;
    }

    public bool TryGetPosition(string senderDeviceId, out PeerProtocolPosition position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderDeviceId);

        if (_peerPositions.TryGetValue(senderDeviceId, out var state))
        {
            lock (state.SyncRoot)
            {
                if (state.Initialized)
                {
                    position = new PeerProtocolPosition(state.Epoch, state.HighestSequence);
                    return true;
                }
            }
        }

        position = default;
        return false;
    }

    public bool ResetPeer(string senderDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderDeviceId);
        return _peerPositions.TryRemove(senderDeviceId, out _);
    }

    private sealed class SequenceState
    {
        public object SyncRoot { get; } = new();

        public bool Initialized { get; set; }

        public long Epoch { get; set; }

        public long HighestSequence { get; set; }
    }
}
