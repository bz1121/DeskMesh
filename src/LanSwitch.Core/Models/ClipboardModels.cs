using System.Text.Json.Serialization;

namespace LanSwitch.Core.Models;

public enum ClipboardDirection
{
    Disabled,
    SendOnly,
    ReceiveOnly,
    Bidirectional
}

public sealed record ClipboardPolicy
{
    public bool Enabled { get; init; } = true;

    public ClipboardDirection Direction { get; init; } = ClipboardDirection.Bidirectional;

    public bool AllowText { get; init; } = true;

    public bool AllowImages { get; init; } = true;

    public bool AllowFiles { get; init; }

    public int MaxTextBytes { get; init; } = 1024 * 1024;

    public int MaxImageBytes { get; init; } = 10 * 1024 * 1024;

    public long MaxFileBytes { get; init; } = 1024L * 1024 * 1024;

    [JsonIgnore]
    public bool CanSend => Enabled && Direction is ClipboardDirection.SendOnly or ClipboardDirection.Bidirectional;

    [JsonIgnore]
    public bool CanReceive => Enabled && Direction is ClipboardDirection.ReceiveOnly or ClipboardDirection.Bidirectional;

    public void Validate()
    {
        if (MaxTextBytes <= 0)
        {
            throw new InvalidOperationException("MaxTextBytes must be positive.");
        }

        if (MaxImageBytes <= 0)
        {
            throw new InvalidOperationException("MaxImageBytes must be positive.");
        }

        if (MaxFileBytes <= 0)
        {
            throw new InvalidOperationException("MaxFileBytes must be positive.");
        }
    }
}

public sealed record FileOffer(
    Guid TransferId,
    string FileName,
    long Length,
    string Sha256,
    int ChunkSize,
    long ChunkCount)
{
    public static FileOffer Create(
        string fileName,
        long length,
        string sha256,
        int chunkSize = 256 * 1024,
        Guid? transferId = null)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        var chunkCount = length == 0 ? 0 : ((length - 1) / chunkSize) + 1;
        return new FileOffer(transferId ?? Guid.NewGuid(), fileName, length, sha256, chunkSize, chunkCount);
    }
}
