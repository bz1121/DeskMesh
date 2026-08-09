using System.Net.Http.Headers;
using System.Security.Cryptography;
using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanSwitch.Agent.Tests;

public sealed class BrowserUploadStagerTests
{
    [Fact]
    public async Task FileFirstMultipartIsReadOnceAndWrittenToOneApplicationSpool()
    {
        var directory = CreateDirectory();
        StagedBrowserUpload? staged = null;
        try
        {
            var payload = Enumerable.Range(0, 128 * 1024).Select(static value => (byte)value).ToArray();
            var request = await CreateRequestAsync(payload, fileFirst: true);
            var source = new CountingNonSeekableStream(request.Body);
            request.Body = source;
            var stager = new BrowserUploadStager(directory, 1024 * 1024);

            staged = await stager.StageAsync(request, Authorize, CancellationToken.None);

            Assert.Equal(payload.Length, staged.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), staged.Sha256);
            Assert.Equal("peer", staged.TargetDeviceId);
            Assert.Equal(payload, await File.ReadAllBytesAsync(staged.SpoolPath));
            Assert.Equal(source.Length, source.BytesRead);
            Assert.Single(Directory.EnumerateFiles(directory, "*.part", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            if (staged is not null && File.Exists(staged.SpoolPath)) File.Delete(staged.SpoolPath);
            Directory.Delete(directory, recursive: false);
        }
    }

    [Fact]
    public async Task OversizedUploadDeletesItsExactPartialFile()
    {
        var directory = CreateDirectory();
        try
        {
            var request = await CreateRequestAsync(new byte[32], fileFirst: false);
            var stager = new BrowserUploadStager(directory, maximumFileBytes: 8);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                stager.StageAsync(request, Authorize, CancellationToken.None));

            Assert.Contains("2 GB", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.part", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(directory, recursive: false);
        }
    }

    [Fact]
    public async Task CancelledUploadDeletesItsExactPartialFile()
    {
        var directory = CreateDirectory();
        try
        {
            var request = await CreateRequestAsync(new byte[128 * 1024], fileFirst: false);
            using var cancellation = new CancellationTokenSource();
            request.Body = new CancelingReadStream(request.Body, cancellation, cancelAfterBytes: 4096);
            var stager = new BrowserUploadStager(directory, 1024 * 1024);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                stager.StageAsync(request, Authorize, cancellation.Token));

            Assert.Empty(Directory.EnumerateFiles(directory, "*.part", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(directory, recursive: false);
        }
    }

    [Fact]
    public async Task TrustRevocationDuringTargetFirstUploadStopsAndCleansTheSpool()
    {
        var directory = CreateDirectory();
        try
        {
            var request = await CreateRequestAsync(new byte[128 * 1024], fileFirst: false);
            using var trust = new CancellationTokenSource();
            request.Body = new CancelingReadStream(request.Body, trust, cancelAfterBytes: 4096);
            var stager = new BrowserUploadStager(directory, 1024 * 1024);

            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                stager.StageAsync(request, targetDeviceId => Authorize(targetDeviceId) with
                {
                    TrustToken = trust.Token
                }, CancellationToken.None));

            Assert.Contains("信任", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.part", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(directory, recursive: false);
        }
    }

    [Fact]
    public async Task ThirdConcurrentStagingRequestFailsFast()
    {
        var directory = CreateDirectory();
        StagedBrowserUpload? firstResult = null;
        StagedBrowserUpload? secondResult = null;
        try
        {
            var first = await CreateBlockedRequestAsync();
            var second = await CreateBlockedRequestAsync();
            var third = await CreateRequestAsync([1, 2, 3], fileFirst: false);
            var stager = new BrowserUploadStager(directory, 1024 * 1024);
            var firstTask = stager.StageAsync(first.Request, Authorize, CancellationToken.None);
            var secondTask = stager.StageAsync(second.Request, Authorize, CancellationToken.None);
            await Task.WhenAll(first.Started.Task, second.Started.Task);

            var error = await Assert.ThrowsAsync<FileTransferBusyException>(() =>
                stager.StageAsync(third, Authorize, CancellationToken.None));
            Assert.Contains("两个文件", error.Message, StringComparison.Ordinal);

            first.Release.TrySetResult();
            second.Release.TrySetResult();
            firstResult = await firstTask;
            secondResult = await secondTask;
        }
        finally
        {
            if (firstResult is not null && File.Exists(firstResult.SpoolPath)) File.Delete(firstResult.SpoolPath);
            if (secondResult is not null && File.Exists(secondResult.SpoolPath)) File.Delete(secondResult.SpoolPath);
            Directory.Delete(directory, recursive: false);
        }
    }

    [Fact]
    public void StartupCleanupDeletesOnlyOldStrictGuidPartNames()
    {
        var directory = CreateDirectory();
        var oldValid = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        var freshValid = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        var oldInvalid = Path.Combine(directory, "not-a-transfer.part");
        try
        {
            File.WriteAllText(oldValid, "old");
            File.WriteAllText(freshValid, "fresh");
            File.WriteAllText(oldInvalid, "keep");
            File.SetLastWriteTimeUtc(oldValid, DateTime.UtcNow - TimeSpan.FromHours(25));
            File.SetLastWriteTimeUtc(oldInvalid, DateTime.UtcNow - TimeSpan.FromHours(25));

            _ = new BrowserUploadStager(directory, 1024);

            Assert.False(File.Exists(oldValid));
            Assert.True(File.Exists(freshValid));
            Assert.True(File.Exists(oldInvalid));
        }
        finally
        {
            if (File.Exists(oldValid)) File.Delete(oldValid);
            if (File.Exists(freshValid)) File.Delete(freshValid);
            if (File.Exists(oldInvalid)) File.Delete(oldInvalid);
            Directory.Delete(directory, recursive: false);
        }
    }

    [Fact]
    public void ExpiryPruneDeletesOnlyInactiveManagedOutgoingSpools()
    {
        var directory = CreateDirectory();
        var outgoing = Path.Combine(directory, "Outgoing");
        Directory.CreateDirectory(outgoing);
        var managedId = Guid.NewGuid().ToString("N");
        var activeId = Guid.NewGuid().ToString("N");
        var incomingId = Guid.NewGuid().ToString("N");
        var outsideId = Guid.NewGuid().ToString("N");
        var unsafeId = Guid.NewGuid().ToString("N");
        var mismatchedOfferId = Guid.NewGuid().ToString("N");
        var managedPath = Path.Combine(outgoing, managedId + ".part");
        var activePath = Path.Combine(outgoing, activeId + ".part");
        var incomingPath = Path.Combine(directory, incomingId + ".part");
        var outsidePath = Path.Combine(directory, outsideId + ".part");
        var nonPartPath = Path.Combine(outgoing, unsafeId + ".txt");
        var mismatchedPath = Path.Combine(outgoing, Guid.NewGuid().ToString("N") + ".part");
        File.WriteAllText(managedPath, "managed");
        File.WriteAllText(activePath, "active");
        File.WriteAllText(incomingPath, "incoming target");
        File.WriteAllText(outsidePath, "outside outgoing");
        File.WriteAllText(nonPartPath, "not a part file");
        File.WriteAllText(mismatchedPath, "belongs to a different id");
        try
        {
            var options = AgentOptions.Parse(["--data-dir", directory]);
            var identity = new DeviceIdentity("local", null!, "LOCAL");
            var settings = new SettingsStore(options, identity);
            var state = new AppState(identity, settings, options);
            var stager = new BrowserUploadStager(outgoing, 1024 * 1024);
            using var coordinator = new FileTransferCoordinator(identity, settings, new PeerDirectory(settings),
                new PeerHttpClientFactory(identity), state, stager, new FileSaveLocationPicker(),
                NullLogger<FileTransferCoordinator>.Instance);
            var now = DateTimeOffset.UtcNow;
            var expired = now - TimeSpan.FromHours(25);
            Assert.True(state.TryAddFile(Offer(managedId, "outgoing", managedPath, expired)));
            Assert.True(state.TryAddFile(Offer(activeId, "outgoing", activePath, expired)));
            Assert.True(state.TryAddFile(Offer(incomingId, "incoming", incomingPath, expired)));
            Assert.True(state.TryAddFile(Offer(outsideId, "outgoing", outsidePath, expired)));
            Assert.True(state.TryAddFile(Offer(unsafeId, "outgoing", nonPartPath, expired)));
            Assert.True(state.TryAddFile(Offer(mismatchedOfferId, "outgoing", mismatchedPath, expired)));

            using (var active = stager.TryAcquireSpoolLease(activeId, activePath))
            {
                Assert.NotNull(active);
                Assert.Equal(5, coordinator.PruneExpiredOffers(now));
                Assert.False(File.Exists(managedPath));
                Assert.True(File.Exists(activePath));
                Assert.True(File.Exists(incomingPath));
                Assert.True(File.Exists(outsidePath));
                Assert.True(File.Exists(nonPartPath));
                Assert.True(File.Exists(mismatchedPath));
                Assert.True(state.TryGetFile(activeId, out _));
            }

            Assert.Equal(1, coordinator.PruneExpiredOffers(now));
            Assert.False(File.Exists(activePath));
            Assert.False(state.TryGetFile(activeId, out _));
        }
        finally
        {
            foreach (var path in new[] { managedPath, activePath, incomingPath, outsidePath, nonPartPath, mismatchedPath })
                if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(outgoing)) Directory.Delete(outgoing, recursive: false);
            Directory.Delete(directory, recursive: false);
        }
    }

    [Fact]
    public void ManagedSpoolDeletionRejectsNonGuidAndOutsidePaths()
    {
        var directory = CreateDirectory();
        var outgoing = Path.Combine(directory, "Outgoing");
        Directory.CreateDirectory(outgoing);
        var valid = Path.Combine(outgoing, Guid.NewGuid().ToString("N") + ".part");
        var nonGuid = Path.Combine(outgoing, "not-a-guid.part");
        var outside = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        File.WriteAllText(valid, "delete");
        File.WriteAllText(nonGuid, "keep");
        File.WriteAllText(outside, "keep");
        try
        {
            var stager = new BrowserUploadStager(outgoing, 1024);

            Assert.True(stager.TryDeleteManagedSpool(Path.GetFileNameWithoutExtension(valid), valid));
            Assert.False(stager.TryDeleteManagedSpool("not-a-guid", nonGuid));
            Assert.False(stager.TryDeleteManagedSpool(Path.GetFileNameWithoutExtension(outside), outside));
            Assert.False(File.Exists(valid));
            Assert.True(File.Exists(nonGuid));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            if (File.Exists(valid)) File.Delete(valid);
            if (File.Exists(nonGuid)) File.Delete(nonGuid);
            if (File.Exists(outside)) File.Delete(outside);
            Directory.Delete(outgoing, recursive: false);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static UploadTargetAuthorization Authorize(string targetDeviceId)
    {
        Assert.Equal("peer", targetDeviceId);
        return new UploadTargetAuthorization(
            new RuntimePeer("peer", "远端", "192.168.1.2", 45832, new string('A', 64), null,
                true, true, 1, DateTimeOffset.UtcNow, ["files"]),
            CancellationToken.None);
    }

    private static FileOfferView Offer(string id, string direction, string path, DateTimeOffset createdAt) =>
        new(id, "test.bin", 4, new string('A', 64), "source", "Source", "target", direction,
            "awaiting-confirmation", createdAt, path);

    private static async Task<HttpRequest> CreateRequestAsync(byte[] file, bool fileFirst)
    {
        using var form = new MultipartFormDataContent("LanSwitchBoundary");
        var fileContent = new ByteArrayContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (fileFirst)
        {
            form.Add(fileContent, "file", "测试.bin");
            form.Add(new StringContent("peer"), "targetDeviceId");
        }
        else
        {
            form.Add(new StringContent("peer"), "targetDeviceId");
            form.Add(fileContent, "file", "测试.bin");
        }
        var body = new MemoryStream();
        await form.CopyToAsync(body);
        body.Position = 0;
        var context = new DefaultHttpContext();
        context.Request.ContentType = form.Headers.ContentType!.ToString();
        context.Request.Body = body;
        return context.Request;
    }

    private static async Task<BlockedRequest> CreateBlockedRequestAsync()
    {
        var request = await CreateRequestAsync([1, 2, 3], fileFirst: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        request.Body = new BlockingReadStream(request.Body, started, release);
        return new BlockedRequest(request, started, release);
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LanSwitch-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record BlockedRequest(
        HttpRequest Request,
        TaskCompletionSource Started,
        TaskCompletionSource Release);

    private class CountingNonSeekableStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancelingReadStream(
        Stream inner,
        CancellationTokenSource cancellation,
        long cancelAfterBytes) : CountingNonSeekableStream(inner)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], cancellationToken);
            if (BytesRead >= cancelAfterBytes) cancellation.Cancel();
            return read;
        }
    }

    private sealed class BlockingReadStream(
        Stream inner,
        TaskCompletionSource started,
        TaskCompletionSource release) : CountingNonSeekableStream(inner)
    {
        private int _blocked;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
