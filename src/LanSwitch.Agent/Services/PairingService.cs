using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

public sealed class PairingService
{
    private readonly DeviceIdentity _identity;
    private readonly SettingsStore _settings;
    private readonly PeerDirectory _peers;
    private readonly PeerHttpClientFactory _clients;
    private readonly AppState _state;
    private readonly AgentOptions _options;
    private readonly ConcurrentDictionary<string, PairingRequestView> _pending = new();
    private readonly object _codeGate = new();
    private readonly object _attemptGate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private string _code = NewCode();
    private DateTimeOffset _codeExpires = DateTimeOffset.UtcNow.AddMinutes(5);

    public PairingService(DeviceIdentity identity, SettingsStore settings, PeerDirectory peers,
        PeerHttpClientFactory clients, AppState state, AgentOptions options)
    {
        _identity = identity;
        _settings = settings;
        _peers = peers;
        _clients = clients;
        _state = state;
        _options = options;
    }

    public LocalPairingCode GetCurrentCode()
    {
        lock (_codeGate)
        {
            if (DateTimeOffset.UtcNow >= _codeExpires)
            {
                _code = NewCodeExcept(_code);
                _codeExpires = DateTimeOffset.UtcNow.AddMinutes(5);
            }
            return new LocalPairingCode(_code, _codeExpires);
        }
    }

    public string CurrentCode => GetCurrentCode().PairingCode;
    public DateTimeOffset CodeExpiresAt => GetCurrentCode().ExpiresAt;
    public IReadOnlyCollection<PairingRequestView> Pending => _pending.Values.OrderByDescending(static item => item.CreatedAt).ToArray();

    public LocalPairingCode RefreshCode()
    {
        lock (_codeGate)
        {
            _code = NewCodeExcept(_code);
            _codeExpires = DateTimeOffset.UtcNow.AddMinutes(5);
            return new LocalPairingCode(_code, _codeExpires);
        }
    }

    public bool ValidateCode(string code)
    {
        var expected = GetCurrentCode().PairingCode;
        var left = System.Text.Encoding.ASCII.GetBytes(code.PadLeft(6, '0'));
        var right = System.Text.Encoding.ASCII.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    public PairingRequestView Receive(IncomingPairRequest request, string address, int port, X509Certificate2 transportCertificate)
    {
        RegisterAttempt(address);
        if (!ConsumeCode(request.Code)) throw new InvalidOperationException("配对码无效或已过期。");
        var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(request.CertificateBase64));
        var fingerprint = DeviceIdentityStore.Fingerprint(certificate);
        if (!string.Equals(fingerprint, request.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("证书指纹不匹配。");
        var transportFingerprint = DeviceIdentityStore.Fingerprint(transportCertificate);
        if (!string.Equals(fingerprint, transportFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配对请求身份与 TLS 客户端证书不一致。");
        var id = Guid.NewGuid().ToString("N");
        var sas = Sas(fingerprint, _identity.Fingerprint);
        var pending = new PairingRequestView(id, "incoming", request.DeviceId, request.DeviceName, address, port,
            fingerprint, request.CertificateBase64, sas, "pending", DateTimeOffset.UtcNow, null);
        _pending[id] = pending;
        _state.Publish("pairing", pending);
        return pending;
    }

    public async Task<PairingRequestView> StartOutgoingAsync(string address, int port, string code, CancellationToken cancellationToken)
    {
        var temporary = new RuntimePeer("pending", address, address, port, string.Empty, null, false, true, null,
            DateTimeOffset.UtcNow, []);
        X509Certificate2? observed = null;
        using var client = _clients.Create(temporary, allowUnpaired: true,
            certificate => observed = X509CertificateLoader.LoadCertificate(certificate.RawData));
        var payload = new IncomingPairRequest(_identity.DeviceId, _settings.Snapshot.DeviceName, _options.PeerPort, code,
            _identity.Fingerprint, _identity.CertificateBase64);
        using var response = await client.PostAsJsonAsync("peer/v1/pairings", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(RemoteErrorMessage(
                await response.Content.ReadAsStringAsync(cancellationToken)));
        var remote = await response.Content.ReadFromJsonAsync<PairingRequestView>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("目标返回了无效的配对响应。");
        if (observed is null) throw new InvalidOperationException("未取得目标设备证书。");
        var observedFingerprint = DeviceIdentityStore.Fingerprint(observed);
        var localId = Guid.NewGuid().ToString("N");
        var pending = remote with
        {
            Id = localId,
            Direction = "outgoing",
            Address = address,
            Port = port,
            Fingerprint = observedFingerprint,
            CertificateBase64 = Convert.ToBase64String(observed.RawData),
            Sas = Sas(_identity.Fingerprint, observedFingerprint),
            CreatedAt = DateTimeOffset.UtcNow,
            RemoteRequestId = remote.Id
        };
        _pending[localId] = pending;
        _state.Publish("pairing", pending);
        _ = PollOutgoingAsync(pending, CancellationToken.None);
        return pending;
    }

    public async Task<PairingRequestView> ConfirmIncomingAsync(string id, bool approve, CancellationToken cancellationToken)
    {
        if (!_pending.TryGetValue(id, out var pending) || pending.Direction != "incoming")
            throw new KeyNotFoundException("找不到配对请求。");
        if (pending.Status != "pending") throw new InvalidOperationException("该配对请求已处理，不能重复确认。");
        if (DateTimeOffset.UtcNow - pending.CreatedAt > TimeSpan.FromMinutes(5))
        {
            var expired = pending with { Status = "expired" };
            _pending.TryUpdate(id, expired, pending);
            throw new InvalidOperationException("配对请求已过期，请重新发起。");
        }
        var claimed = pending with { Status = approve ? "approving" : "rejected", Error = null };
        if (!_pending.TryUpdate(id, claimed, pending))
            throw new InvalidOperationException("该配对请求正在由另一项操作处理。");
        if (!approve)
        {
            _state.Publish("pairing", claimed);
            return claimed;
        }
        try
        {
            await _peers.StorePairAsync(new StoredPeer(pending.DeviceId, pending.DeviceName, pending.Address, pending.Port,
                pending.Fingerprint, pending.CertificateBase64, DateTimeOffset.UtcNow), cancellationToken);
            var approved = claimed with { Status = "approved" };
            _pending[id] = approved;
            _state.Publish("pairing", approved);
            return approved;
        }
        catch (Exception exception)
        {
            var retryable = pending with { Error = exception.Message };
            _pending.TryUpdate(id, retryable, claimed);
            throw;
        }
    }

    public PairingStatusResponse GetRemoteStatus(string id)
    {
        if (!_pending.TryGetValue(id, out var pending) || pending.Direction != "incoming")
            throw new KeyNotFoundException("找不到配对请求。");
        if (pending.Status == "pending" && DateTimeOffset.UtcNow - pending.CreatedAt > TimeSpan.FromMinutes(5))
        {
            var expired = pending with { Status = "expired" };
            if (_pending.TryUpdate(id, expired, pending)) pending = expired;
            else if (_pending.TryGetValue(id, out var current)) pending = current;
        }
        var remoteStatus = pending.Status == "approving" ? "pending" : pending.Status;
        return new PairingStatusResponse(remoteStatus, _identity.DeviceId, _settings.Snapshot.DeviceName,
            _identity.Fingerprint, _identity.CertificateBase64, pending.Sas, _options.PeerPort);
    }

    private async Task PollOutgoingAsync(PairingRequestView pending, CancellationToken cancellationToken)
    {
        try
        {
            var remote = new RuntimePeer(pending.DeviceId, pending.DeviceName, pending.Address, pending.Port,
                pending.Fingerprint, pending.CertificateBase64, true, true, null, DateTimeOffset.UtcNow, []);
            using var client = _clients.Create(remote);
            var polling = Stopwatch.StartNew();
            while (polling.Elapsed < TimeSpan.FromMinutes(5) && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                PairingStatusResponse? status;
                try
                {
                    status = await client.GetFromJsonAsync<PairingStatusResponse>(
                        $"peer/v1/pairings/{pending.RemoteRequestId}/status", cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    continue;
                }
                if (status is null) throw new InvalidOperationException("目标返回了无效的配对状态。");
                if (status.Status == "pending") continue;
                if (status.Status == "approved")
                {
                    var returnedCertificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(status.CertificateBase64));
                    var returnedFingerprint = DeviceIdentityStore.Fingerprint(returnedCertificate);
                    if (!string.Equals(status.Fingerprint, pending.Fingerprint, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(returnedFingerprint, pending.Fingerprint, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("配对确认中的服务端证书已变化，已拒绝建立信任。");
                    await _peers.StorePairAsync(new StoredPeer(status.DeviceId, status.DeviceName, pending.Address,
                        pending.Port, pending.Fingerprint, pending.CertificateBase64, DateTimeOffset.UtcNow), cancellationToken);
                }
                var updated = pending with { Status = status.Status, DeviceId = status.DeviceId, DeviceName = status.DeviceName };
                _pending[pending.Id] = updated;
                _state.Publish("pairing", updated);
                return;
            }
            var expired = pending with { Status = "expired" };
            _pending[pending.Id] = expired;
            _state.Publish("pairing", expired);
        }
        catch (Exception exception)
        {
            var failed = pending with { Status = "failed", Error = exception.Message };
            _pending[pending.Id] = failed;
            _state.Publish("pairing", failed);
        }
    }

    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    private static string NewCodeExcept(string previous)
    {
        string next;
        do next = NewCode(); while (next == previous);
        return next;
    }

    internal static string RemoteErrorMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "目标设备拒绝了配对请求。";
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in new[] { "detail", "error", "message", "title" })
                {
                    if (document.RootElement.TryGetProperty(property, out var value) &&
                        value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(value.GetString()))
                        return value.GetString()!;
                }
            }
        }
        catch (JsonException) { }

        return content.Length <= 512 ? content : content[..512];
    }

    private bool ConsumeCode(string code)
    {
        lock (_codeGate)
        {
            if (DateTimeOffset.UtcNow >= _codeExpires) return false;
            var left = System.Text.Encoding.ASCII.GetBytes(code.PadLeft(6, '0'));
            var right = System.Text.Encoding.ASCII.GetBytes(_code);
            if (left.Length != right.Length || !CryptographicOperations.FixedTimeEquals(left, right)) return false;
            _code = NewCodeExcept(_code);
            _codeExpires = DateTimeOffset.UtcNow.AddMinutes(5);
            return true;
        }
    }

    private void RegisterAttempt(string address)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_attemptGate)
        {
            foreach (var key in _attempts.Where(pair => pair.Value.Count == 0 || now - pair.Value.Last() > TimeSpan.FromMinutes(10))
                         .Select(static pair => pair.Key).ToArray())
                _attempts.Remove(key);
            if (!_attempts.TryGetValue(address, out var attempts)) _attempts[address] = attempts = new Queue<DateTimeOffset>();
            while (attempts.TryPeek(out var oldest) && now - oldest > TimeSpan.FromMinutes(1)) attempts.Dequeue();
            if (attempts.Count >= 8) throw new InvalidOperationException("配对尝试过于频繁，请稍后再试。");
            attempts.Enqueue(now);
        }
    }
    private static string Sas(string left, string right)
    {
        var ordered = string.CompareOrdinal(left, right) <= 0 ? left + right : right + left;
        var bytes = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(ordered));
        string[] words = ["白云", "星河", "青松", "晨光", "远山", "清风", "海浪", "月影"];
        return $"{words[bytes[0] % words.Length]}-{words[bytes[1] % words.Length]}-{bytes[2]:D3}";
    }
}

public sealed record IncomingPairRequest(string DeviceId, string DeviceName, int Port, string Code, string Fingerprint, string CertificateBase64);
public sealed record PairingStatusResponse(string Status, string DeviceId, string DeviceName, string Fingerprint,
    string CertificateBase64, string Sas, int Port);
public sealed record LocalPairingCode(string PairingCode, DateTimeOffset ExpiresAt);
public sealed record PairingRequestView(string Id, string Direction, string DeviceId, string DeviceName, string Address,
    int Port, string Fingerprint, string CertificateBase64, string Sas, string Status, DateTimeOffset CreatedAt,
    string? RemoteRequestId, string? Error = null);
