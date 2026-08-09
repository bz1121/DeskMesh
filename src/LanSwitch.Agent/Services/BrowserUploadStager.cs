using System.Security.Cryptography;
using System.Text;
using LanSwitch.Agent.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace LanSwitch.Agent.Services;

public sealed class BrowserUploadStager
{
    private const int TargetFieldLimitBytes = 4096;
    private static readonly TimeSpan OrphanMaximumAge = TimeSpan.FromHours(24);

    private readonly string _outgoingDirectory;
    private readonly long _maximumFileBytes;
    private readonly SemaphoreSlim _stagingSlots;
    private readonly TimeProvider _timeProvider;
    private readonly object _spoolGate = new();
    private readonly Dictionary<string, int> _activeSpools = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cleanupSpools = new(StringComparer.OrdinalIgnoreCase);

    public BrowserUploadStager(AgentOptions options)
        : this(Path.Combine(options.DataDirectory, "Outgoing"), AgentOptions.MaxFileBytes, 2, TimeProvider.System)
    {
    }

    internal BrowserUploadStager(
        string outgoingDirectory,
        long maximumFileBytes,
        int maximumConcurrentUploads = 2,
        TimeProvider? timeProvider = null)
    {
        if (maximumFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        if (maximumConcurrentUploads <= 0) throw new ArgumentOutOfRangeException(nameof(maximumConcurrentUploads));
        _outgoingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outgoingDirectory));
        _maximumFileBytes = maximumFileBytes;
        _stagingSlots = new SemaphoreSlim(maximumConcurrentUploads, maximumConcurrentUploads);
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_outgoingDirectory);
        CleanupOrphanedParts();
    }

    public async Task<StagedBrowserUpload> StageAsync(
        HttpRequest request,
        Func<string, UploadTargetAuthorization> authorizeTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorizeTarget);
        if (!await _stagingSlots.WaitAsync(0, cancellationToken))
            throw new FileTransferBusyException("当前已有两个文件正在本地暂存，请稍后重试。");

        string? spoolPath = null;
        UploadTargetAuthorization? authorization = null;
        try
        {
            var boundary = GetBoundary(request.ContentType);
            var reader = new MultipartReader(boundary, request.Body)
            {
                BodyLengthLimit = AgentOptions.MaxFileRequestBytes,
                HeadersCountLimit = 8,
                HeadersLengthLimit = 16 * 1024
            };
            string? targetDeviceId = null;
            string? fileName = null;
            string? sha256 = null;
            long fileLength = 0;
            var fileCount = 0;
            var targetCount = 0;

            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                    !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("multipart 段缺少有效的 form-data 描述。");

                var fieldName = Unquote(disposition.Name.Value);
                var suppliedFileName = Unquote(disposition.FileNameStar.Value) ?? Unquote(disposition.FileName.Value);
                if (!string.IsNullOrWhiteSpace(suppliedFileName))
                {
                    if (!string.Equals(fieldName, "file", StringComparison.Ordinal) || ++fileCount != 1)
                        throw new InvalidOperationException("一次请求只允许包含一个名为 file 的文件。");
                    fileName = FileTransferCoordinator.SafeFileName(suppliedFileName);
                    var id = Guid.NewGuid().ToString("N");
                    spoolPath = Path.Combine(_outgoingDirectory, id + ".part");
                    using var linked = authorization is null
                        ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                        : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorization.TrustToken);
                    (fileLength, sha256) = await CopyFileAsync(section.Body, spoolPath, linked.Token);
                    continue;
                }

                if (!string.Equals(fieldName, "targetDeviceId", StringComparison.Ordinal) || ++targetCount != 1)
                    throw new InvalidOperationException("一次请求只允许包含一个 targetDeviceId 字段和一个文件。");
                targetDeviceId = (await ReadSmallTextFieldAsync(section.Body, cancellationToken)).Trim();
                if (string.IsNullOrWhiteSpace(targetDeviceId))
                    throw new InvalidOperationException("目标设备不能为空。");
                authorization = authorizeTarget(targetDeviceId);
                authorization.TrustToken.ThrowIfCancellationRequested();
            }

            if (fileCount != 1 || targetCount != 1 || spoolPath is null || fileName is null || sha256 is null ||
                targetDeviceId is null || authorization is null)
                throw new InvalidOperationException("请求必须恰好包含一个文件和一个 targetDeviceId 字段。");
            authorization.TrustToken.ThrowIfCancellationRequested();
            return new StagedBrowserUpload(
                Path.GetFileNameWithoutExtension(spoolPath),
                fileName,
                fileLength,
                sha256,
                spoolPath,
                targetDeviceId,
                authorization);
        }
        catch (OperationCanceledException) when (
            authorization?.TrustToken.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            if (spoolPath is not null) TryDeleteGeneratedSpool(spoolPath);
            throw new UnauthorizedAccessException("目标设备的信任已在上传期间撤销。");
        }
        catch (InvalidDataException exception)
        {
            if (spoolPath is not null) TryDeleteGeneratedSpool(spoolPath);
            throw new InvalidOperationException("multipart 文件上传格式无效。", exception);
        }
        catch
        {
            if (spoolPath is not null) TryDeleteGeneratedSpool(spoolPath);
            throw;
        }
        finally
        {
            _stagingSlots.Release();
        }
    }

    internal int CleanupOrphanedParts()
    {
        var removed = 0;
        var cutoff = _timeProvider.GetUtcNow() - OrphanMaximumAge;
        string[] candidates;
        try { candidates = Directory.GetFiles(_outgoingDirectory, "*.part", SearchOption.TopDirectoryOnly); }
        catch { return 0; }

        foreach (var path in candidates)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) >= cutoff.UtcDateTime) continue;
                var id = Path.GetFileNameWithoutExtension(path);
                if (TryDeleteManagedSpool(id, path)) removed++;
            }
            catch
            {
                // An active or locked file is left untouched and can be reconsidered next start.
            }
        }
        return removed;
    }

    internal BrowserUploadSpoolLease? TryAcquireSpoolLease(string offerId, string? path)
    {
        if (!TryResolveManagedSpoolPath(offerId, path, out var resolved)) return null;
        lock (_spoolGate)
        {
            if (_cleanupSpools.Contains(resolved) || !File.Exists(resolved)) return null;
            _activeSpools.TryGetValue(resolved, out var count);
            _activeSpools[resolved] = count + 1;
            return new BrowserUploadSpoolLease(this, resolved);
        }
    }

    internal BrowserUploadCleanupLease? TryAcquireCleanupLease(string offerId, string? path, out bool managedPath)
    {
        managedPath = TryResolveManagedSpoolPath(offerId, path, out var resolved);
        if (!managedPath) return null;
        lock (_spoolGate)
        {
            if (_cleanupSpools.Contains(resolved) || _activeSpools.ContainsKey(resolved)) return null;
            _cleanupSpools.Add(resolved);
            return new BrowserUploadCleanupLease(this, resolved);
        }
    }

    internal bool TryDeleteManagedSpool(string offerId, string? path)
    {
        using var cleanup = TryAcquireCleanupLease(offerId, path, out var managedPath);
        return managedPath && cleanup is not null && cleanup.TryDelete();
    }

    private bool TryDeleteGeneratedSpool(string path)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        return TryDeleteManagedSpool(id, path);
    }

    private async Task<(long Length, string Sha256)> CopyFileAsync(
        Stream input,
        string spoolPath,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            spoolPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total = checked(total + read);
            if (total > _maximumFileBytes)
                throw new InvalidOperationException("文件超过 2 GB 上限。");
            hasher.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total == 0) throw new InvalidOperationException("不能发送空文件。");
        await output.FlushAsync(cancellationToken);
        return (total, Convert.ToHexString(hasher.GetHashAndReset()));
    }

    private static async Task<string> ReadSmallTextFieldAsync(Stream input, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(TargetFieldLimitBytes);
        var chunk = new byte[512];
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > TargetFieldLimitBytes)
                throw new InvalidOperationException("targetDeviceId 字段过长。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static string GetBoundary(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType) ||
            !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请使用 multipart/form-data 上传文件。");
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary)) throw new InvalidOperationException("multipart boundary 无效。");
        if (boundary.Length > 128) throw new InvalidOperationException("multipart boundary 过长。");
        return boundary;
    }

    private static string? Unquote(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');

    private bool TryResolveManagedSpoolPath(string offerId, string? path, out string resolved)
    {
        resolved = string.Empty;
        if (!Guid.TryParseExact(offerId, "N", out _) || string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path)) return false;
        try { resolved = Path.GetFullPath(path); }
        catch { return false; }
        if (!string.Equals(Path.GetDirectoryName(resolved), _outgoingDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(resolved), ".part", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileNameWithoutExtension(resolved), offerId, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    internal bool TryDeleteReserved(string path)
    {
        lock (_spoolGate)
        {
            if (!_cleanupSpools.Contains(path) || _activeSpools.ContainsKey(path)) return false;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return !File.Exists(path);
            }
            catch { return false; }
        }
    }

    internal bool TryDeleteFromActiveLease(string path)
    {
        lock (_spoolGate)
        {
            if (!_activeSpools.TryGetValue(path, out var count) || count != 1 || _cleanupSpools.Contains(path))
                return false;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return !File.Exists(path);
            }
            catch { return false; }
        }
    }

    internal void ReleaseSpoolLease(string path)
    {
        lock (_spoolGate)
        {
            if (!_activeSpools.TryGetValue(path, out var count)) return;
            if (count <= 1) _activeSpools.Remove(path);
            else _activeSpools[path] = count - 1;
        }
    }

    internal void ReleaseCleanupLease(string path)
    {
        lock (_spoolGate) _cleanupSpools.Remove(path);
    }
}

internal sealed class BrowserUploadSpoolLease(BrowserUploadStager owner, string path) : IDisposable
{
    private int _disposed;
    public bool TryDelete() => Volatile.Read(ref _disposed) == 0 && owner.TryDeleteFromActiveLease(path);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ReleaseSpoolLease(path);
    }
}

internal sealed class BrowserUploadCleanupLease(BrowserUploadStager owner, string path) : IDisposable
{
    private int _disposed;
    public bool TryDelete() => Volatile.Read(ref _disposed) == 0 && owner.TryDeleteReserved(path);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ReleaseCleanupLease(path);
    }
}

public sealed record UploadTargetAuthorization(RuntimePeer Peer, CancellationToken TrustToken);

public sealed record StagedBrowserUpload(
    string Id,
    string FileName,
    long Length,
    string Sha256,
    string SpoolPath,
    string TargetDeviceId,
    UploadTargetAuthorization Authorization);
