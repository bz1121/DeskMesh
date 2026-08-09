using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Core.Files;
using CoreFileOffer = LanSwitch.Core.Models.FileOffer;

namespace LanSwitch.Agent.Services;

public sealed class FileTransferCoordinator(
    DeviceIdentity identity,
    SettingsStore settings,
    PeerDirectory peers,
    PeerHttpClientFactory clients,
    AppState state,
    BrowserUploadStager browserUploads,
    FileSaveLocationPicker saveLocationPicker,
    ILogger<FileTransferCoordinator> logger) : IDisposable
{
    private static readonly TimeSpan ExpirySweepInterval = TimeSpan.FromMinutes(5);
    private readonly object _transferGatesLock = new();
    private readonly Dictionary<string, TransferGateEntry> _transferGates = new(StringComparer.Ordinal);
    private System.Threading.Timer? _expiryTimer;
    private int _disposed;
    private readonly string _incomingDirectory = EnsureDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DeskMesh"));

    public IReadOnlyCollection<FileOfferView> Offers
    {
        get
        {
            EnsureExpiryCleanupStarted();
            PruneExpiredOffers();
            return state.Files;
        }
    }

    public async Task<FileOfferView> AcceptBrowserUploadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        EnsureExpiryCleanupStarted();
        PruneExpiredOffers();
        StagedBrowserUpload? staged = null;
        FileOfferView? trackedOffer = null;
        try
        {
            staged = await browserUploads.StageAsync(request, targetDeviceId =>
            {
                if (!peers.TryGet(targetDeviceId, out var candidate) || !candidate.Paired || !candidate.Online)
                    throw new InvalidOperationException("目标设备未配对或当前离线。");
                if (!peers.TryGetTrustToken(candidate.Id, candidate.Fingerprint, out var candidateTrustToken))
                    throw new UnauthorizedAccessException("目标设备的信任已被撤销。");
                return new UploadTargetAuthorization(candidate, candidateTrustToken);
            }, cancellationToken);
            var peer = staged.Authorization.Peer;
            var offer = new FileOfferView(staged.Id, staged.FileName, staged.Length, staged.Sha256,
                identity.DeviceId, settings.Snapshot.DeviceName, peer.Id, "outgoing", "awaiting-confirmation",
                DateTimeOffset.UtcNow, staged.SpoolPath);
            if (!state.TryAddFile(offer)) throw new InvalidOperationException("文件队列已满，请清理或等待旧任务过期。");
            trackedOffer = offer;
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                staged.Authorization.TrustToken);
            using var client = clients.Create(peer);
            using var response = await client.PostAsJsonAsync("peer/v1/files/offers", FileOfferPacket.FromView(offer), requestCancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                state.UpsertFile(offer with { Status = "failed", Error = await response.Content.ReadAsStringAsync(cancellationToken) });
                throw new InvalidOperationException("目标设备未接受文件提议。");
            }
            return offer;
        }
        catch (Exception exception)
        {
            if (staged is not null) browserUploads.TryDeleteManagedSpool(staged.Id, staged.SpoolPath);
            if (trackedOffer is not null)
                state.UpsertFile(trackedOffer with { Status = "failed", Error = exception.Message });
            throw;
        }
    }

    public FileOfferView ReceiveOffer(FileOfferPacket packet, RuntimePeer source)
    {
        EnsureExpiryCleanupStarted();
        PruneExpiredOffers();
        if (!string.Equals(packet.SourceDeviceId, source.Id, StringComparison.Ordinal) ||
            !string.Equals(packet.TargetDeviceId, identity.DeviceId, StringComparison.Ordinal))
            throw new InvalidOperationException("文件提议的设备身份不匹配。");
        if (!Guid.TryParseExact(packet.Id, "N", out var transferId) || packet.SizeBytes is <= 0 or > AgentOptions.MaxFileBytes)
            throw new InvalidOperationException("文件大小或摘要不合法。");
        var coreOffer = CoreFileOffer.Create(packet.FileName, packet.SizeBytes, packet.Sha256, 1024 * 1024, transferId);
        if (!FileTransferValidator.ValidateOffer(coreOffer).IsValid)
            throw new InvalidOperationException("文件名、大小或摘要未通过协议校验。");
        var offer = new FileOfferView(packet.Id, SafeFileName(packet.FileName), packet.SizeBytes, packet.Sha256,
            source.Id, source.Name, identity.DeviceId, "incoming", "awaiting-confirmation", DateTimeOffset.UtcNow);
        if (!state.TryAddFile(offer)) throw new InvalidOperationException("文件提议编号重复。");
        return offer;
    }

    public async Task<FileOfferView> DecideAsync(string id, bool accept, CancellationToken cancellationToken)
    {
        if (!state.TryGetFile(id, out _)) throw new KeyNotFoundException("找不到待接收文件。");
        using (await AcquireTransferGateAsync(id, cancellationToken))
        {
            if (!state.TryGetFile(id, out var offer) || offer.Direction != "incoming")
                throw new KeyNotFoundException("找不到待接收文件。");
            var retryDecision = offer.Status == "decision-failed" &&
                ((accept && offer.LocalPath is not null) || (!accept && offer.LocalPath is null));
            if (offer.Status != "awaiting-confirmation" && !retryDecision)
                throw new InvalidOperationException("该文件提议已处理。");
            if (!peers.TryGet(offer.SourceDeviceId, out var source) || !source.Paired)
                throw new InvalidOperationException("来源设备未配对。");
            if (!peers.TryGetTrustToken(source.Id, source.Fingerprint, out var trustToken))
            {
                state.UpsertFile(offer with { Status = "failed", Error = "来源设备的信任已被撤销。" });
                throw new UnauthorizedAccessException("来源设备的信任已被撤销。");
            }
            var destination = retryDecision
                ? offer.LocalPath
                : accept
                ? await saveLocationPicker.PickAsync(_incomingDirectory, offer.FileName, cancellationToken)
                : null;
            var accepted = accept && destination is not null;
            var updated = offer with
            {
                Status = accepted ? "accepted" : accept ? "cancelled" : "rejected",
                LocalPath = destination
            };
            state.UpsertFile(updated);
            try
            {
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, trustToken);
                using var client = clients.Create(source);
                using var response = await client.PostAsJsonAsync($"peer/v1/files/offers/{Uri.EscapeDataString(id)}/decision",
                    new FileDecisionPacket(accepted, destination is null ? null : Path.GetFileName(destination)), requestCancellation.Token);
                response.EnsureSuccessStatusCode();
                return updated;
            }
            catch (Exception exception)
            {
                state.UpsertFile(updated with { Status = "decision-failed", Error = exception.Message });
                throw;
            }
        }
    }

    public async Task<FileOfferView> HandleRemoteDecisionAsync(string id, FileDecisionPacket decision, RuntimePeer source,
        CancellationToken cancellationToken)
    {
        if (!state.TryGetFile(id, out _)) throw new KeyNotFoundException("找不到待发送文件。");
        using (await AcquireTransferGateAsync(id, cancellationToken))
        {
            if (!state.TryGetFile(id, out var offer) || offer.Direction != "outgoing" || offer.LocalPath is null)
                throw new KeyNotFoundException("找不到待发送文件。");
            if (offer.Status != "awaiting-confirmation")
            {
                if ((decision.Accept && offer.Status is "sending" or "completed") ||
                    (!decision.Accept && offer.Status == "rejected")) return offer;
                throw new InvalidOperationException("该文件提议已处理。");
            }
            if (!string.Equals(offer.TargetDeviceId, source.Id, StringComparison.Ordinal))
                throw new InvalidOperationException("文件决定的设备身份不匹配。");
            if (!decision.Accept)
            {
                var rejected = offer with { Status = "rejected" };
                state.UpsertFile(rejected);
                browserUploads.TryDeleteManagedSpool(offer.Id, offer.LocalPath);
                return rejected;
            }
            if (!peers.TryGet(offer.TargetDeviceId, out var target)) throw new InvalidOperationException("目标设备不可用。");
            var spoolLease = browserUploads.TryAcquireSpoolLease(offer.Id, offer.LocalPath) ??
                throw new InvalidOperationException("本机暂存文件已失效或正在清理。");
            var sending = offer with { Status = "sending" };
            state.UpsertFile(sending);
            _ = SendContentAsync(target, sending, spoolLease, CancellationToken.None);
            return sending;
        }
    }

    public async Task<FileOfferView> ReceiveContentAsync(string id, Stream body, RuntimePeer source,
        CancellationToken cancellationToken)
    {
        if (!state.TryGetFile(id, out _)) throw new KeyNotFoundException("找不到待接收文件。");
        using (await AcquireTransferGateAsync(id, cancellationToken))
        {
            if (!state.TryGetFile(id, out var offer) || offer.Direction != "incoming" ||
                offer.Status is not ("accepted" or "decision-failed") || offer.LocalPath is null)
                throw new InvalidOperationException("文件尚未在目标端确认。");
            if (!string.Equals(offer.SourceDeviceId, source.Id, StringComparison.Ordinal))
                throw new InvalidOperationException("文件内容的设备身份不匹配。");
            if (!peers.TryGetTrustToken(source.Id, source.Fingerprint, out var trustToken))
            {
                state.UpsertFile(offer with { Status = "failed", Error = "来源设备的信任已被撤销。" });
                throw new UnauthorizedAccessException("来源设备的信任已被撤销。");
            }
            using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, trustToken);
            var transferToken = transferCancellation.Token;
            var partPath = offer.LocalPath + ".part";
            long total = 0;
            string actualHash;
            try
            {
            await using var output = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, transferToken)) > 0)
            {
                if (!peers.IsAuthorized(source.Id, source.Fingerprint))
                    throw new UnauthorizedAccessException("来源设备的信任已被撤销。");
                total += read;
                if (total > AgentOptions.MaxFileBytes || total > offer.SizeBytes) throw new InvalidOperationException("接收数据超过声明大小。");
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), transferToken);
            }
            actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            await output.FlushAsync(transferToken);
            if (total != offer.SizeBytes || !string.Equals(actualHash, offer.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("文件完整性校验失败。");
            output.Close();
            File.Move(partPath, offer.LocalPath);
            var completed = offer with { Status = "completed" };
            state.UpsertFile(completed);
            return completed;
            }
            catch (Exception exception)
            {
                TryDeleteExact(partPath);
                var failed = offer with { Status = "failed", Error = exception.Message };
                state.UpsertFile(failed);
                throw;
            }
        }
    }

    private async Task SendContentAsync(RuntimePeer target, FileOfferView offer, BrowserUploadSpoolLease spoolLease,
        CancellationToken cancellationToken)
    {
        using (spoolLease)
        {
            try
            {
                if (!peers.TryGetTrustToken(target.Id, target.Fingerprint, out var trustToken))
                    throw new UnauthorizedAccessException("目标设备的信任已被撤销。");
                using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, trustToken);
                using (var client = clients.Create(target))
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    await using var stream = new FileStream(offer.LocalPath!, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 1024 * 1024, true);
                    using var content = new StreamContent(stream, 1024 * 1024);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.ContentLength = offer.SizeBytes;
                    using var response = await client.PutAsync(
                        $"peer/v1/files/offers/{Uri.EscapeDataString(offer.Id)}/content", content,
                        transferCancellation.Token);
                    response.EnsureSuccessStatusCode();
                }
                spoolLease.TryDelete();
                state.UpsertFile(offer with { Status = "completed" });
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "文件 {File} 发送失败", offer.FileName);
                spoolLease.TryDelete();
                state.UpsertFile(offer with { Status = "failed", Error = exception.Message });
            }
        }
    }

    internal int PruneExpiredOffers(DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var candidate in state.GetExpiredFiles(now))
        {
            if (!TryAcquireTransferGate(candidate.Id, out var transferLease)) continue;
            using (transferLease)
            {
                if (!state.TryGetFile(candidate.Id, out var current) || !AppState.IsFileExpired(current, now)) continue;
                if (string.Equals(current.Direction, "outgoing", StringComparison.Ordinal) && current.LocalPath is not null)
                {
                    using var cleanup = browserUploads.TryAcquireCleanupLease(
                        current.Id, current.LocalPath, out var managedPath);
                    if (managedPath && cleanup is null) continue;
                    if (cleanup is not null && !cleanup.TryDelete()) continue;
                }
                if (state.TryRemoveExpiredFile(current.Id, now, out _)) removed++;
            }
        }
        return removed;
    }

    private void EnsureExpiryCleanupStarted()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _expiryTimer) is not null) return;
        var timer = new System.Threading.Timer(static owner => ((FileTransferCoordinator)owner!).RunExpirySweep(), this,
            ExpirySweepInterval, ExpirySweepInterval);
        if (Interlocked.CompareExchange(ref _expiryTimer, timer, null) is not null) timer.Dispose();
    }

    private void RunExpirySweep()
    {
        try { PruneExpiredOffers(); }
        catch (Exception exception) { logger.LogWarning(exception, "清理过期文件暂存失败"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _expiryTimer, null)?.Dispose();
    }

    private static string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }
    internal static string SafeFileName(string value)
    {
        var name = Path.GetFileName(value);
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "未命名文件" : name;
    }
    private static string UniqueDestination(string directory, string fileName)
    {
        return UniqueDestinationForPath(Path.Combine(directory, fileName));
    }
    internal static string UniqueDestinationForPath(string requestedPath)
    {
        var directory = Path.GetDirectoryName(requestedPath) ?? throw new ArgumentException("保存目录无效。", nameof(requestedPath));
        var fileName = Path.GetFileName(requestedPath);
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path) && !File.Exists(path + ".part")) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 10_000; index++)
        {
            path = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(path) && !File.Exists(path + ".part")) return path;
        }
        throw new IOException("无法生成不重复的文件名。");
    }
    private static void TryDeleteExact(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private async Task<IDisposable> AcquireTransferGateAsync(string id, CancellationToken cancellationToken)
    {
        TransferGateEntry entry;
        lock (_transferGatesLock)
        {
            if (!_transferGates.TryGetValue(id, out entry!))
            {
                entry = new TransferGateEntry();
                _transferGates[id] = entry;
            }
            if (entry.ReferenceCount >= 4)
                throw new FileTransferBusyException("同一文件任务的并发请求过多，请稍后重试。");
            entry.ReferenceCount++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            return new TransferGateLease(this, id, entry);
        }
        catch
        {
            ReleaseTransferGate(id, entry, acquired: false);
            throw;
        }
    }

    private bool TryAcquireTransferGate(string id, out IDisposable? lease)
    {
        TransferGateEntry entry;
        lock (_transferGatesLock)
        {
            if (!_transferGates.TryGetValue(id, out entry!))
            {
                entry = new TransferGateEntry();
                _transferGates[id] = entry;
            }
            entry.ReferenceCount++;
        }
        if (entry.Semaphore.Wait(0))
        {
            lease = new TransferGateLease(this, id, entry);
            return true;
        }
        ReleaseTransferGate(id, entry, acquired: false);
        lease = null;
        return false;
    }

    private void ReleaseTransferGate(string id, TransferGateEntry entry, bool acquired)
    {
        if (acquired) entry.Semaphore.Release();
        lock (_transferGatesLock)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 && _transferGates.TryGetValue(id, out var current) && ReferenceEquals(current, entry))
            {
                _transferGates.Remove(id);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class TransferGateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class TransferGateLease(FileTransferCoordinator owner, string id, TransferGateEntry entry) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.ReleaseTransferGate(id, entry, acquired: true);
        }
    }
}

public sealed record FileOfferPacket(string Id, string FileName, long SizeBytes, string Sha256, string SourceDeviceId,
    string SourceDeviceName, string TargetDeviceId)
{
    public static FileOfferPacket FromView(FileOfferView view) => new(view.Id, view.FileName, view.SizeBytes, view.Sha256,
        view.SourceDeviceId, view.SourceDeviceName, view.TargetDeviceId);
}
public sealed record FileDecisionPacket(bool Accept, string? SuggestedName);
public sealed class FileTransferBusyException(string message) : Exception(message);
