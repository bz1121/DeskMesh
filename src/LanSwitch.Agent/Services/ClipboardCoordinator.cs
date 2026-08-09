using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Core.Clipboard;

namespace LanSwitch.Agent.Services;

public sealed class ClipboardCoordinator(
    DeviceIdentity identity,
    SettingsStore settings,
    PeerDirectory peers,
    PeerHttpClientFactory clients,
    AppState state,
    ILogger<ClipboardCoordinator> logger)
{
    private readonly object _orderGate = new();
    private readonly Dictionary<(string Source, string Session), long> _sourceSequences = new();
    private readonly Dictionary<string, string> _sourceSessions = new(StringComparer.Ordinal);
    private readonly ClipboardLoopGuard _loopGuard = new(capacity: 128, fallbackTtl: TimeSpan.FromSeconds(5));
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private long _logicalClock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public object GetPolicy()
    {
        var value = settings.Snapshot;
        return new
        {
            enabled = value.ClipboardEnabled,
            textEnabled = value.ClipboardTextEnabled,
            imageEnabled = value.ClipboardImageEnabled,
            direction = value.ClipboardDirection,
            maxTextBytes = value.MaxTextBytes,
            maxImageBytes = value.MaxImageBytes,
            latest = state.Clipboard.Latest
        };
    }

    public async Task<object> UpdatePolicyAsync(ClipboardPolicyRequest request, CancellationToken cancellationToken)
    {
        var updated = await settings.UpdateAsync(current => current with
        {
            ClipboardEnabled = request.Enabled ?? current.ClipboardEnabled,
            ClipboardTextEnabled = request.TextEnabled ?? current.ClipboardTextEnabled,
            ClipboardImageEnabled = request.ImageEnabled ?? current.ClipboardImageEnabled,
            ClipboardDirection = NormalizeDirection(request.Direction ?? current.ClipboardDirection),
            MaxTextBytes = Math.Clamp(request.MaxTextBytes ?? current.MaxTextBytes, 1024, 1024 * 1024),
            MaxImageBytes = Math.Clamp(request.MaxImageBytes ?? current.MaxImageBytes, 1024, 20 * 1024 * 1024)
        }, cancellationToken);
        state.SetClipboard(state.Clipboard with { Enabled = updated.ClipboardEnabled, Direction = updated.ClipboardDirection });
        return GetPolicy();
    }

    public async Task PublishLocalAsync(string kind, byte[] data, string? textPreview, CancellationToken cancellationToken)
    {
        var config = settings.Snapshot;
        if (!config.ClipboardEnabled || config.ClipboardDirection == "receive-only") return;
        if (kind == "text" && (!config.ClipboardTextEnabled || data.Length > config.MaxTextBytes)) return;
        if (kind == "image" && (!config.ClipboardImageEnabled || data.Length > config.MaxImageBytes)) return;
        var now = DateTimeOffset.UtcNow;
        var digest = ClipboardLoopGuard.ComputeDigest(kind, data);
        if (_loopGuard.ShouldSuppress(digest, marker: null, now)) return;
        var hash = Convert.ToHexString(SHA256.HashData(data));
        var sequence = NextLocalSequence();
        var packet = new ClipboardPacket(identity.DeviceId, config.DeviceName, _sessionId, sequence,
            kind, Convert.ToBase64String(data), hash, data.Length);
        state.SetClipboard(new ClipboardView(true, config.ClipboardDirection,
            new ClipboardLatestView(kind, textPreview, data.Length, config.DeviceName, DateTimeOffset.UtcNow, hash)));
        foreach (var peer in peers.PairedPeers.Where(static peer => peer.Online))
        {
            if (!peers.TryGetTrustToken(peer.Id, peer.Fingerprint, out var trustToken)) continue;
            try
            {
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, trustToken);
                using var client = clients.Create(peer);
                using var response = await client.PostAsJsonAsync("peer/v1/clipboard", packet, requestCancellation.Token);
                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException) when (trustToken.IsCancellationRequested)
            {
                logger.LogDebug("已取消向撤销信任的设备 {Peer} 同步剪贴板", peer.Name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "向 {Peer} 同步剪贴板失败", peer.Name);
            }
        }
    }

    public ClipboardPacket? ReceiveRemote(ClipboardPacket packet, RuntimePeer source)
    {
        var config = settings.Snapshot;
        if (!config.ClipboardEnabled || config.ClipboardDirection == "send-only") return null;
        if (!string.Equals(packet.SourceDeviceId, source.Id, StringComparison.Ordinal) ||
            packet.Kind is not ("text" or "image") || !Guid.TryParseExact(packet.SessionId, "N", out _) ||
            packet.Sequence <= 0 || packet.SizeBytes < 0)
            return null;
        var limit = packet.Kind == "text" ? config.MaxTextBytes : config.MaxImageBytes;
        if (packet.SizeBytes > limit || packet.DataBase64.Length > ((long)limit + 2) / 3 * 4 + 8) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(packet.DataBase64); }
        catch (FormatException) { return null; }
        if (bytes.Length != packet.SizeBytes) return null;
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, packet.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !TryAcceptRemoteOrder(packet.Sequence, source.Id, packet.SessionId)) return null;
        if (packet.Kind == "text" && (!config.ClipboardTextEnabled || bytes.Length > config.MaxTextBytes)) return null;
        if (packet.Kind == "image" && (!config.ClipboardImageEnabled || bytes.Length > config.MaxImageBytes)) return null;
        var preview = packet.Kind == "text" ? SafePreview(Encoding.UTF8.GetString(bytes)) : null;
        var digest = ClipboardLoopGuard.ComputeDigest(packet.Kind, bytes);
        _loopGuard.RememberRemoteApplication(new ClipboardOrigin(source.Id, packet.Sequence, digest), DateTimeOffset.UtcNow);
        state.SetClipboard(new ClipboardView(true, config.ClipboardDirection,
            new ClipboardLatestView(packet.Kind, preview, bytes.Length, source.Name, DateTimeOffset.UtcNow, actualHash)));
        return packet with
        {
            SourceDeviceId = source.Id,
            SourceName = source.Name,
            DataBase64 = Convert.ToBase64String(bytes),
            Sha256 = actualHash
        };
    }

    public void ClearLatest() => state.SetClipboard(state.Clipboard with { Latest = null });

    private long NextLocalSequence()
    {
        lock (_orderGate)
        {
            return ++_logicalClock;
        }
    }

    private bool TryAcceptRemoteOrder(long sequence, string source, string session)
    {
        lock (_orderGate)
        {
            if (_sourceSessions.TryGetValue(source, out var previousSession) && previousSession != session)
            {
                foreach (var key in _sourceSequences.Keys.Where(key => key.Source == source).ToArray())
                    _sourceSequences.Remove(key);
            }
            _sourceSessions[source] = session;
            var sourceKey = (source, session);
            if (_sourceSequences.TryGetValue(sourceKey, out var lastSourceSequence) && sequence <= lastSourceSequence)
                return false;
            _sourceSequences[sourceKey] = sequence;
            return true;
        }
    }

    public static string SafePreview(string value) => value.Length <= 240 ? value : value[..240] + "…";
    private static string NormalizeDirection(string value) => value is "send-only" or "receive-only" ? value : "bidirectional";
}

public sealed record ClipboardPolicyRequest(bool? Enabled, bool? TextEnabled, bool? ImageEnabled, string? Direction,
    int? MaxTextBytes, int? MaxImageBytes);
public sealed record ClipboardPacket(string SourceDeviceId, string SourceName, string SessionId, long Sequence, string Kind,
    string DataBase64, string Sha256, int SizeBytes);
