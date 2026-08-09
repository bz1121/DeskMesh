using System.Collections.Concurrent;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

public sealed class PeerDirectory
{
    private readonly SettingsStore _settings;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, RuntimePeer> _runtime = new();
    private readonly ConcurrentDictionary<string, byte> _revokedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _trustSessions = new(StringComparer.Ordinal);

    public PeerDirectory(SettingsStore settings)
    {
        _settings = settings;
        var snapshot = settings.Snapshot;
        foreach (var fingerprint in snapshot.RevokedFingerprints ?? [])
            _revokedFingerprints[fingerprint] = 0;
        foreach (var peer in snapshot.Peers)
        {
            _runtime[peer.Id] = RuntimePeer.FromStored(peer);
            if (!_revokedFingerprints.ContainsKey(peer.Fingerprint)) _trustSessions[peer.Id] = new CancellationTokenSource();
        }
    }

    public IReadOnlyList<object> Snapshot()
    {
        PruneExpiredDiscoveries(DateTimeOffset.UtcNow);
        return _runtime.Values
            .Where(peer => !peer.Paired || !IsRevoked(peer))
            .OrderByDescending(static peer => peer.Online)
            .ThenBy(static peer => peer.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static peer => (object)peer.ToApi())
            .ToArray();
    }

    public IReadOnlyList<RuntimePeer> PairedPeers => _runtime.Values.Where(peer => peer.Paired && !IsRevoked(peer)).ToArray();
    public bool HasPairedPeers => _runtime.Values.Any(peer => peer.Paired && !IsRevoked(peer));
    public bool TryGet(string id, out RuntimePeer peer) => _runtime.TryGetValue(id, out peer!) &&
        (!peer.Paired || !IsRevoked(peer));
    public RuntimePeer? FindByFingerprint(string fingerprint) => _runtime.Values.FirstOrDefault(peer => peer.Paired &&
        !_revokedFingerprints.ContainsKey(peer.Fingerprint) &&
        string.Equals(peer.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
    public bool IsAuthorized(string id, string fingerprint) => !_revokedFingerprints.ContainsKey(fingerprint) &&
        _runtime.TryGetValue(id, out var peer) && peer.Paired &&
        string.Equals(peer.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);
    public bool TryGetTrustToken(string id, string fingerprint, out CancellationToken token)
    {
        token = default;
        if (!IsAuthorized(id, fingerprint) || !_trustSessions.TryGetValue(id, out var session)) return false;
        token = session.Token;
        return !token.IsCancellationRequested && IsAuthorized(id, fingerprint);
    }

    public RuntimePeer? Observe(DiscoveryBeacon beacon, string address)
    {
        var now = DateTimeOffset.UtcNow;
        PruneExpiredDiscoveries(now);
        if (_runtime.TryGetValue(beacon.DeviceId, out var paired) && paired.Paired)
            return IsRevoked(paired) ? null : paired;
        if (_revokedFingerprints.ContainsKey(beacon.Fingerprint))
            return null;
        if (!_runtime.ContainsKey(beacon.DeviceId) && _runtime.Values.Count(static peer => !peer.Paired) >= AgentOptions.MaxDiscoveredPeers)
            return null;
        return _runtime.AddOrUpdate(beacon.DeviceId,
            _ => new RuntimePeer(beacon.DeviceId, beacon.Name, address, beacon.Port, beacon.Fingerprint, null, false,
                true, null, now, beacon.Capabilities),
            (_, current) => current with
            {
                Name = beacon.Name,
                Address = address,
                Port = beacon.Port,
                Fingerprint = current.Paired ? current.Fingerprint : beacon.Fingerprint,
                Online = true,
                LastSeen = now,
                Capabilities = beacon.Capabilities
            });
    }

    internal int PruneExpiredDiscoveries(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var stale in _runtime.Values.Where(peer =>
                     !peer.Paired && now - peer.LastSeen > AgentOptions.DiscoveredPeerTtl))
        {
            if (_runtime.TryRemove(stale.Id, out _)) removed++;
        }
        return removed;
    }

    public void MarkHeartbeat(string id, double latencyMs)
    {
        if (_runtime.TryGetValue(id, out var peer))
            _runtime[id] = peer with { Online = true, LatencyMs = Math.Round(latencyMs, 1), LastSeen = DateTimeOffset.UtcNow };
    }

    public void MarkOffline(string id)
    {
        if (_runtime.TryGetValue(id, out var peer)) _runtime[id] = peer with { Online = false, LatencyMs = null };
    }

    public async Task<RuntimePeer> StorePairAsync(StoredPeer stored, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var conflictingIdentity = _runtime.Values.FirstOrDefault(peer => peer.Paired &&
                ((peer.Id != stored.Id && string.Equals(peer.Fingerprint, stored.Fingerprint, StringComparison.OrdinalIgnoreCase)) ||
                 (peer.Id == stored.Id && !string.Equals(peer.Fingerprint, stored.Fingerprint, StringComparison.OrdinalIgnoreCase))));
            if (conflictingIdentity is not null)
                throw new InvalidOperationException("设备编号或证书已绑定其他配对关系，请先忘记旧设备。");
            await _settings.UpdateAsync(settings => settings with
            {
                Peers = [.. settings.Peers.Where(peer => peer.Id != stored.Id), stored],
                RevokedFingerprints = [.. (settings.RevokedFingerprints ?? [])
                    .Where(fingerprint => !string.Equals(fingerprint, stored.Fingerprint, StringComparison.OrdinalIgnoreCase))]
            }, cancellationToken);
            var runtime = RuntimePeer.FromStored(stored) with { Online = true, LastSeen = DateTimeOffset.UtcNow };
            _revokedFingerprints.TryRemove(stored.Fingerprint, out _);
            _runtime[stored.Id] = runtime;
            var replacement = new CancellationTokenSource();
            if (_trustSessions.TryGetValue(stored.Id, out var previous)) previous.Cancel();
            _trustSessions[stored.Id] = replacement;
            return runtime;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<bool> PromoteVerifiedAddressAsync(string id, string fingerprint, string address, int port,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_runtime.TryGetValue(id, out var current) || !current.Paired ||
                !string.Equals(current.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) return false;
            var stored = _settings.Snapshot.Peers.FirstOrDefault(peer => peer.Id == id);
            if (stored is null) return false;
            var updated = current with { Address = address, Port = port, Online = true, LastSeen = DateTimeOffset.UtcNow };
            await _settings.UpdateAsync(settings => settings with
            {
                Peers = [.. settings.Peers.Where(peer => peer.Id != id),
                    stored with { Address = updated.Address, Port = updated.Port }]
            }, cancellationToken);
            _runtime[id] = updated;
            return true;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<bool> RemoveAsync(string id, Action<RuntimePeer> afterRevoked,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        RuntimePeer? revoked = null;
        try
        {
            if (!_runtime.TryGetValue(id, out revoked)) return false;
            if (!string.IsNullOrWhiteSpace(revoked.Fingerprint))
                _revokedFingerprints[revoked.Fingerprint] = 0;
            if (_trustSessions.TryRemove(revoked.Id, out var trustSession)) trustSession.Cancel();
            afterRevoked(revoked);
            await _settings.UpdateAsync(settings => settings with
            {
                Peers = [.. settings.Peers.Where(peer => peer.Id != id)],
                RevokedFingerprints = [.. (settings.RevokedFingerprints ?? [])
                    .Append(revoked.Fingerprint)
                    .Where(static fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
                    .Distinct(StringComparer.OrdinalIgnoreCase)]
            }, CancellationToken.None);
            _runtime.TryRemove(id, out _);
            return true;
        }
        catch
        {
            if (revoked is not null)
            {
                _revokedFingerprints.TryRemove(revoked.Fingerprint, out _);
                _trustSessions[revoked.Id] = new CancellationTokenSource();
            }
            throw;
        }
        finally { _mutationGate.Release(); }
    }

    private bool IsRevoked(RuntimePeer peer) => _revokedFingerprints.ContainsKey(peer.Fingerprint);
}

public sealed record RuntimePeer(
    string Id,
    string Name,
    string Address,
    int Port,
    string Fingerprint,
    string? CertificateBase64,
    bool Paired,
    bool Online,
    double? LatencyMs,
    DateTimeOffset LastSeen,
    string[] Capabilities)
{
    public static RuntimePeer FromStored(StoredPeer peer) => new(peer.Id, peer.Name, peer.Address, peer.Port,
        peer.Fingerprint, peer.CertificateBase64, true, false, null, DateTimeOffset.MinValue,
        ["clipboard", "files", "input", "display", "audio", "remote-desktop"]);

    public object ToApi() => new
    {
        id = Id,
        name = Name,
        address = $"{Address}:{Port}",
        online = Online,
        latencyMs = LatencyMs,
        lastSeen = LastSeen,
        capabilities = Capabilities,
        paired = Paired,
        fingerprint = Fingerprint
    };
}

public sealed record DiscoveryBeacon(string Service, int Protocol, string DeviceId, string Name, int Port,
    string Fingerprint, string[] Capabilities);
