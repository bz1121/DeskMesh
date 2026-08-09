using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LanSwitch.Core.Internal;

namespace LanSwitch.Core.Clipboard;

public readonly record struct ClipboardDigest
{
    public ClipboardDigest(string format, long length, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        Format = format.Trim().ToLowerInvariant();
        Length = length;
        Sha256 = HexUtility.NormalizeSha256(sha256);
    }

    public string Format { get; }

    public long Length { get; }

    public string Sha256 { get; }

    public string CacheKey => $"{Format}:{Length}:{Sha256}";
}

public sealed record ClipboardOrigin(string DeviceId, long Sequence, ClipboardDigest Digest)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DeviceId);
        if (Sequence <= 0)
        {
            throw new InvalidOperationException("Clipboard origin sequence must be greater than zero.");
        }
    }
}

public sealed class ClipboardLoopGuard
{
    public static readonly TimeSpan DefaultFallbackTtl = TimeSpan.FromSeconds(5);

    private readonly object _syncRoot = new();
    private readonly int _capacity;
    private readonly TimeSpan _fallbackTtl;
    private readonly Dictionary<string, LinkedListNode<RememberedDigest>> _entries =
        new(StringComparer.Ordinal);
    private readonly LinkedList<RememberedDigest> _leastRecentlyUsed = new();

    public ClipboardLoopGuard(int capacity = 128, TimeSpan? fallbackTtl = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _fallbackTtl = fallbackTtl ?? DefaultFallbackTtl;
        if (_fallbackTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackTtl));
        }
    }

    public static ClipboardDigest ComputeDigest(string format, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);

        var normalizedFormat = format.Trim().ToLowerInvariant();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(normalizedFormat));
        hash.AppendData([0]);

        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(lengthBytes, content.Length);
        hash.AppendData(lengthBytes);
        hash.AppendData(content);

        return new ClipboardDigest(normalizedFormat, content.Length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    public void RememberRemoteApplication(
        ClipboardOrigin origin,
        DateTimeOffset appliedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(origin);
        origin.Validate();

        lock (_syncRoot)
        {
            PruneExpired(appliedAtUtc);
            Remember(origin.Digest, appliedAtUtc + _fallbackTtl);
        }
    }

    public bool ShouldSuppress(
        ClipboardDigest digest,
        ClipboardOrigin? marker,
        DateTimeOffset observedAtUtc)
    {
        if (marker is not null)
        {
            marker.Validate();
            if (marker.Digest == digest)
            {
                return true;
            }
        }

        lock (_syncRoot)
        {
            PruneExpired(observedAtUtc);
            if (!_entries.TryGetValue(digest.CacheKey, out var node))
            {
                return false;
            }

            _leastRecentlyUsed.Remove(node);
            _leastRecentlyUsed.AddLast(node);
            return node.Value.ExpiresAtUtc > observedAtUtc;
        }
    }

    private void Remember(ClipboardDigest digest, DateTimeOffset expiresAtUtc)
    {
        if (_entries.TryGetValue(digest.CacheKey, out var existing))
        {
            existing.Value = existing.Value with { ExpiresAtUtc = expiresAtUtc };
            _leastRecentlyUsed.Remove(existing);
            _leastRecentlyUsed.AddLast(existing);
            return;
        }

        var entry = new RememberedDigest(digest.CacheKey, expiresAtUtc);
        var node = _leastRecentlyUsed.AddLast(entry);
        _entries.Add(entry.CacheKey, node);

        while (_entries.Count > _capacity)
        {
            var oldest = _leastRecentlyUsed.First!;
            _leastRecentlyUsed.RemoveFirst();
            _entries.Remove(oldest.Value.CacheKey);
        }
    }

    private void PruneExpired(DateTimeOffset nowUtc)
    {
        var node = _leastRecentlyUsed.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.ExpiresAtUtc <= nowUtc)
            {
                _leastRecentlyUsed.Remove(node);
                _entries.Remove(node.Value.CacheKey);
            }

            node = next;
        }
    }

    private sealed record RememberedDigest(string CacheKey, DateTimeOffset ExpiresAtUtc);
}
