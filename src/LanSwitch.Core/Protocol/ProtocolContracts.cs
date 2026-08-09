using System.Text.Json;

namespace LanSwitch.Core.Protocol;

public static class ProtocolConstants
{
    public const int MinimumSupportedVersion = 1;

    public const int CurrentVersion = 1;
}

public enum ProtocolMessageType
{
    Hello,
    Heartbeat,
    PairingRequest,
    PairingConfirm,
    FocusPrepare,
    FocusReady,
    FocusCommit,
    FocusAbort,
    InputBatch,
    ReleaseAll,
    ClipboardOffer,
    ClipboardAccept,
    ClipboardChunk,
    ClipboardCommit,
    FileOffer,
    FileAccept,
    FileChunk,
    FileCommit,
    DisplaySwitch,
    DisplayResult,
    Error
}

public sealed record ProtocolEnvelope
{
    public int Version { get; init; } = ProtocolConstants.CurrentVersion;

    public ProtocolMessageType Type { get; init; }

    public Guid MessageId { get; init; } = Guid.NewGuid();

    public required string SenderDeviceId { get; init; }

    public long Epoch { get; init; }

    public long Sequence { get; init; }

    public DateTimeOffset SentAtUtc { get; init; }

    public JsonElement? Payload { get; init; }
}

public enum ProtocolValidationError
{
    None,
    UnsupportedVersion,
    MissingMessageId,
    MissingSenderDeviceId,
    InvalidEpoch,
    ActiveEpochRequired,
    InvalidSequence,
    FirstSequenceMustBeOne,
    StaleEpoch,
    ReplayOrOutOfOrder
}

public readonly record struct ProtocolValidationResult(
    bool IsValid,
    ProtocolValidationError Error,
    string? Message)
{
    public static ProtocolValidationResult Success { get; } = new(true, ProtocolValidationError.None, null);

    public static ProtocolValidationResult Failure(ProtocolValidationError error, string message)
    {
        return new ProtocolValidationResult(false, error, message);
    }
}

public readonly record struct PeerProtocolPosition(long Epoch, long HighestSequence);
