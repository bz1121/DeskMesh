using System.Collections;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using LanSwitch.Agent.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace LanSwitch.Agent.Tests;

public sealed class LocalAdminServiceTests
{
    private const string Password = "Correct-Horse-2026!";
    private const string NewPassword = "Changed-Horse-2026!";

    [Fact]
    public async Task FirstSetupRequiresAOneTimeTrayBootstrapAndPersistsOnlyDpapiCiphertext()
    {
        using var context = TestContext.Create();
        Assert.Equal(AdminCredentialState.Missing, context.Service.CredentialState);
        Assert.Equal("invalid-bootstrap-token", (await context.Service.SetupAsync(null, Password, null)).Status);

        var bootstrap = context.Service.IssueBootstrapToken();
        var setup = await context.Service.SetupAsync(null, Password, bootstrap.Token);

        Assert.True(setup.Succeeded);
        Assert.Equal("admin", setup.Username);
        Assert.True(File.Exists(context.Service.CredentialPath));
        var raw = File.ReadAllBytes(context.Service.CredentialPath);
        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.DoesNotContain("admin", Encoding.UTF8.GetString(raw), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("conflict", (await context.Service.SetupAsync("other", Password, bootstrap.Token)).Status);

        var restarted = context.CreateService();
        Assert.Equal(AdminCredentialState.Ready, restarted.CredentialState);
        Assert.False(restarted.Authenticate(setup.Token).IsAuthenticated);
        Assert.True((await restarted.LoginAsync("ADMIN", Password)).Succeeded);
    }

    [Fact]
    public async Task AdministratorPasswordAcceptsSixCharactersAndRejectsFive()
    {
        using var tooShortContext = TestContext.Create();
        var tooShortBootstrap = tooShortContext.Service.IssueBootstrapToken();
        var tooShort = await Assert.ThrowsAsync<ArgumentException>(() =>
            tooShortContext.Service.SetupAsync("admin", "12345", tooShortBootstrap.Token));
        Assert.Contains("6-256", tooShort.Message, StringComparison.Ordinal);

        using var minimumContext = TestContext.Create();
        var minimumBootstrap = minimumContext.Service.IssueBootstrapToken();
        var setup = await minimumContext.Service.SetupAsync("admin", "123456", minimumBootstrap.Token);

        Assert.True(setup.Succeeded);
        Assert.True((await minimumContext.Service.LoginAsync("admin", "123456")).Succeeded);

        var authentication = minimumContext.Service.Authenticate(setup.Token);
        var changed = await minimumContext.Service.ChangePasswordAsync(
            authentication.SessionId!, "123456", "654321");
        Assert.True(changed.Succeeded);
        Assert.True((await minimumContext.Service.LoginAsync("admin", "654321")).Succeeded);
    }

    [Fact]
    public async Task BootstrapExpiresAndCanOnlyBeReissuedByTheTrayService()
    {
        using var context = TestContext.Create();
        var expired = context.Service.IssueBootstrapToken();
        context.Time.Advance(LocalAdminService.BootstrapLifetime + TimeSpan.FromSeconds(1));

        Assert.Equal("invalid-bootstrap-token",
            (await context.Service.SetupAsync("admin", Password, expired.Token)).Status);

        var current = context.Service.IssueBootstrapToken();
        Assert.True((await context.Service.SetupAsync("admin", Password, current.Token)).Succeeded);
    }

    [Fact]
    public async Task ReissuingBootstrapImmediatelyInvalidatesThePreviousGrant()
    {
        using var context = TestContext.Create();
        var previous = context.Service.IssueBootstrapToken();
        var current = context.Service.IssueBootstrapToken();

        Assert.Equal("invalid-bootstrap-token",
            (await context.Service.SetupAsync("admin", Password, previous.Token)).Status);
        Assert.True((await context.Service.SetupAsync("admin", Password, current.Token)).Succeeded);
    }

    [Fact]
    public async Task ConcurrentSetupWithOneGrantCanCreateOnlyOneAdministrator()
    {
        using var context = TestContext.Create();
        var bootstrap = context.Service.IssueBootstrapToken();

        var results = await Task.WhenAll(
            context.Service.SetupAsync("admin", Password, bootstrap.Token),
            context.Service.SetupAsync("admin", Password, bootstrap.Token));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => result.Status == "conflict");
    }

    [Fact]
    public void SessionCookieIsHostOnlyHttpOnlyStrictAndScopedToTheAgentInstance()
    {
        using var first = TestContext.Create();
        using var second = TestContext.Create();
        Assert.NotEqual(first.Service.CookieName, second.Service.CookieName);
        Assert.StartsWith("DeskMesh.Admin.", first.Service.CookieName, StringComparison.Ordinal);

        var appendContext = new DefaultHttpContext();
        first.Service.AppendSessionCookie(
            appendContext.Response,
            "opaque-session-token",
            DateTimeOffset.UtcNow.AddMinutes(15));
        var appended = appendContext.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{first.Service.CookieName}=opaque-session-token", appended, StringComparison.Ordinal);
        Assert.Contains("path=/", appended, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", appended, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", appended, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", appended, StringComparison.OrdinalIgnoreCase);

        var deleteContext = new DefaultHttpContext();
        first.Service.DeleteSessionCookie(deleteContext.Response);
        var deleted = deleteContext.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{first.Service.CookieName}=", deleted, StringComparison.Ordinal);
        Assert.Contains("path=/", deleted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", deleted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", deleted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", deleted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptOrFutureCredentialFilesFailClosedUntilTrayReset()
    {
        using var context = TestContext.Create();
        File.WriteAllBytes(context.CredentialPath, "not-a-dpapi-envelope"u8.ToArray());
        File.WriteAllText(context.SettingsSentinelPath, "preserve settings");
        File.WriteAllText(context.IdentitySentinelPath, "preserve identity");
        var corrupt = context.CreateService();

        Assert.Equal(AdminCredentialState.RecoveryRequired, corrupt.CredentialState);
        Assert.Throws<InvalidOperationException>(corrupt.IssueBootstrapToken);
        Assert.Equal("recovery-required", (await corrupt.LoginAsync("admin", Password)).Status);
        Assert.Equal("recovery-required",
            (await corrupt.SetupAsync("admin", Password, "anything")).Status);

        await corrupt.ResetCredentialAsync();

        Assert.Equal(AdminCredentialState.Missing, corrupt.CredentialState);
        Assert.True(File.Exists(context.SettingsSentinelPath));
        Assert.True(File.Exists(context.IdentitySentinelPath));
        var bootstrap = corrupt.IssueBootstrapToken();
        Assert.True((await corrupt.SetupAsync("admin", Password, bootstrap.Token)).Succeeded);
    }

    [Fact]
    public void FutureCredentialSchemaFailsClosedInsteadOfStartingSetup()
    {
        using var context = TestContext.Create();
        var bytes = new byte[AdminCredentialStore.HeaderLength + 1];
        "DESKAUTH"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, sizeof(int)),
            AdminCredentialStore.SchemaVersion + 1);
        File.WriteAllBytes(context.CredentialPath, bytes);

        var future = context.CreateService();

        Assert.Equal(AdminCredentialState.RecoveryRequired, future.CredentialState);
        Assert.Equal("recovery-required", future.GetStatus((string?)null).State);
        Assert.Throws<InvalidOperationException>(future.IssueBootstrapToken);
    }

    [Fact]
    public async Task LockoutIsPersistedAcrossAgentRestart()
    {
        using var context = TestContext.Create();
        var bootstrap = context.Service.IssueBootstrapToken();
        var setup = await context.Service.SetupAsync("admin", Password, bootstrap.Token);
        context.Service.Logout(setup.Token);

        AdminLoginResult? last = null;
        for (var attempt = 0; attempt < LocalAdminService.MaximumFailures; attempt++)
            last = await context.Service.LoginAsync("admin", "Definitely-Wrong-2026!");

        Assert.Equal("locked", last!.Status);
        var restarted = context.CreateService();
        Assert.Equal("locked", (await restarted.LoginAsync("admin", Password)).Status);
        var lockedStatus = restarted.GetStatus((string?)null);
        Assert.Equal(LocalAdminService.MaximumFailures, lockedStatus.FailedAttempts);
        Assert.NotNull(lockedStatus.LockedUntil);

        context.Time.Advance(LocalAdminService.LockoutDuration + TimeSpan.FromSeconds(1));
        Assert.True((await restarted.LoginAsync("admin", Password)).Succeeded);
        Assert.Equal(0, restarted.GetStatus((string?)null).FailedAttempts);
    }

    [Fact]
    public async Task SessionTokensAreHashedAndRevocationRemainsObservableByConcurrentRequests()
    {
        using var context = TestContext.Create();
        var bootstrap = context.Service.IssueBootstrapToken();
        var setup = await context.Service.SetupAsync("admin", Password, bootstrap.Token);
        var first = context.Service.Authenticate(setup.Token);
        var secondLogin = await context.Service.LoginAsync("admin", Password);
        var second = context.Service.Authenticate(secondLogin.Token);

        var tokenMap = Assert.IsAssignableFrom<IDictionary>(typeof(LocalAdminService)
            .GetField("_tokensByHash", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context.Service));
        Assert.False(tokenMap.Contains(setup.Token!));
        Assert.False(tokenMap.Contains(secondLogin.Token!));
        Assert.NotEqual(first.CsrfToken, second.CsrfToken);

        Assert.Equal(1, context.Service.RevokeOtherSessions(first.SessionId!, includeCurrent: false));
        Assert.True(second.RevocationToken.IsCancellationRequested);
        using var linkedAfterRevoke = CancellationTokenSource.CreateLinkedTokenSource(second.RevocationToken);
        Assert.True(linkedAfterRevoke.IsCancellationRequested);
        Assert.True(first.IsAuthenticated);

        context.Service.Logout(setup.Token);
        Assert.True(first.RevocationToken.IsCancellationRequested);
        Assert.False(context.Service.Authenticate(setup.Token).IsAuthenticated);
    }

    [Fact]
    public async Task SessionCapacityRevokesTheOldestAndTokenRotationDoesNotIncreaseTheCount()
    {
        using var context = TestContext.Create();
        var bootstrap = context.Service.IssueBootstrapToken();
        var setup = await context.Service.SetupAsync("admin", Password, bootstrap.Token);
        var oldest = context.Service.Authenticate(setup.Token);
        AdminLoginResult newest = setup;

        for (var index = 0; index < LocalAdminService.MaximumActiveSessions; index++)
        {
            context.Time.Advance(TimeSpan.FromSeconds(1));
            newest = await context.Service.LoginAsync("admin", Password);
            Assert.True(newest.Succeeded);
        }

        Assert.True(oldest.RevocationToken.IsCancellationRequested);
        Assert.False(context.Service.Authenticate(setup.Token).IsAuthenticated);
        var capped = context.Service.GetStatus(newest.Token);
        Assert.Equal(LocalAdminService.MaximumActiveSessions, capped.ActiveSessionCount);

        context.Time.Advance(LocalAdminService.RotationInterval + TimeSpan.FromSeconds(1));
        var rotated = context.Service.Authenticate(newest.Token);
        Assert.NotNull(rotated.ReplacementToken);
        Assert.Equal(
            LocalAdminService.MaximumActiveSessions,
            context.Service.GetStatus(rotated.ReplacementToken).ActiveSessionCount);
    }

    [Fact]
    public async Task RevokedCurrentSessionCannotUseAStaleRequestToRevokeOtherSessions()
    {
        using var context = TestContext.Create();
        var bootstrap = context.Service.IssueBootstrapToken();
        var firstLogin = await context.Service.SetupAsync("admin", Password, bootstrap.Token);
        var first = context.Service.Authenticate(firstLogin.Token);
        var secondLogin = await context.Service.LoginAsync("admin", Password);
        var second = context.Service.Authenticate(secondLogin.Token);
        var thirdLogin = await context.Service.LoginAsync("admin", Password);
        var third = context.Service.Authenticate(thirdLogin.Token);

        context.Service.Logout(firstLogin.Token);

        Assert.Throws<UnauthorizedAccessException>(() =>
            context.Service.RevokeOtherSessions(first.SessionId!, includeCurrent: false));
        Assert.True(context.Service.Authenticate(secondLogin.Token).IsAuthenticated);
        Assert.True(context.Service.Authenticate(thirdLogin.Token).IsAuthenticated);
        Assert.Equal(1, context.Service.RevokeOtherSessions(second.SessionId!, includeCurrent: false));
        Assert.True(third.RevocationToken.IsCancellationRequested);
        Assert.True(context.Service.Authenticate(secondLogin.Token).IsAuthenticated);
    }

    [Fact]
    public async Task PollingDoesNotExtendIdleButAnAuthenticatedTouchDoes()
    {
        using var context = TestContext.Create(
            idle: TimeSpan.FromMinutes(10),
            absolute: TimeSpan.FromMinutes(30),
            rotation: TimeSpan.FromMinutes(2));
        var bootstrap = context.Service.IssueBootstrapToken();
        var setup = await context.Service.SetupAsync("admin", Password, bootstrap.Token);

        context.Time.Advance(TimeSpan.FromMinutes(6));
        var touched = context.Service.Authenticate(setup.Token, allowRotation: false, extendIdle: true);
        Assert.True(touched.IsAuthenticated);
        Assert.Equal(context.Time.GetUtcNow() + TimeSpan.FromMinutes(10), touched.ExpiresAt);

        context.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.True(context.Service.Authenticate(setup.Token, allowRotation: false).IsAuthenticated);
        context.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.False(context.Service.Authenticate(setup.Token, allowRotation: false).IsAuthenticated);
        Assert.True(touched.RevocationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task UserActivityCannotExtendASessionPastItsAbsoluteLifetime()
    {
        using var context = TestContext.Create(
            idle: TimeSpan.FromMinutes(10),
            absolute: TimeSpan.FromMinutes(15),
            rotation: TimeSpan.FromMinutes(2));
        var bootstrap = context.Service.IssueBootstrapToken();
        var setup = await context.Service.SetupAsync("admin", Password, bootstrap.Token);

        context.Time.Advance(TimeSpan.FromMinutes(9));
        var touched = context.Service.Authenticate(setup.Token, allowRotation: false, extendIdle: true);
        Assert.Equal(context.Time.GetUtcNow() + TimeSpan.FromMinutes(6), touched.ExpiresAt);
        context.Time.Advance(TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(1));

        Assert.False(context.Service.Authenticate(setup.Token, allowRotation: false, extendIdle: true).IsAuthenticated);
        Assert.True(touched.RevocationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task PasswordChangeRevokesEveryOldSessionAndIssuesANewOne()
    {
        using var context = TestContext.Create();
        var bootstrap = context.Service.IssueBootstrapToken();
        var setup = await context.Service.SetupAsync("admin", Password, bootstrap.Token);
        var oldSession = context.Service.Authenticate(setup.Token);
        var secondLogin = await context.Service.LoginAsync("admin", Password);
        var secondSession = context.Service.Authenticate(secondLogin.Token);

        var changed = await context.Service.ChangePasswordAsync(
            oldSession.SessionId!, Password, NewPassword);

        Assert.True(changed.Succeeded);
        Assert.True(oldSession.RevocationToken.IsCancellationRequested);
        Assert.True(secondSession.RevocationToken.IsCancellationRequested);
        Assert.Equal("invalid-credentials", (await context.Service.LoginAsync("admin", Password)).Status);
        Assert.True((await context.Service.LoginAsync("admin", NewPassword)).Succeeded);
    }

    [Fact]
    public async Task SafetyNotificationFiresOnlyWhenTheFinalActiveSessionEnds()
    {
        using var context = TestContext.Create();
        var notifications = 0;
        context.Service.LastSessionEnded += () => Interlocked.Increment(ref notifications);
        var bootstrap = context.Service.IssueBootstrapToken();
        var first = await context.Service.SetupAsync("admin", Password, bootstrap.Token);
        var second = await context.Service.LoginAsync("admin", Password);

        context.Service.Logout(first.Token);
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref notifications));

        context.Service.Logout(second.Token);
        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref notifications) == 1,
            TimeSpan.FromSeconds(2)));
        Assert.Equal(1, Volatile.Read(ref notifications));
    }

    [Fact]
    public async Task IdleExpirationNotifiesSafetyWithoutAnotherApiRequest()
    {
        using var context = TestContext.Create(
            idle: TimeSpan.FromMilliseconds(100),
            absolute: TimeSpan.FromSeconds(1),
            rotation: TimeSpan.FromMilliseconds(50));
        var notifications = 0;
        context.Service.LastSessionEnded += () => Interlocked.Increment(ref notifications);
        var bootstrap = context.Service.IssueBootstrapToken();
        var setup = await context.Service.SetupAsync("admin", Password, bootstrap.Token);

        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref notifications) == 1,
            TimeSpan.FromSeconds(3)));
        Assert.False(context.Service.Authenticate(setup.Token).IsAuthenticated);
        Assert.Equal(1, Volatile.Read(ref notifications));
    }

    [Theory]
    [InlineData("/api/v1/session", false)]
    [InlineData("/api/v1/auth/status", false)]
    [InlineData("/api/v1/auth/setup", false)]
    [InlineData("/api/v1/auth/login", false)]
    [InlineData("/api/v1/auth/logout", true)]
    [InlineData("/api/v1/auth/touch", true)]
    [InlineData("/api/v1/status", true)]
    [InlineData("/api/v1/events", true)]
    public void OnlyBootstrapAndLoginDiscoveryRoutesAreAnonymous(string path, bool expected) =>
        Assert.Equal(expected, ApiEndpoints.RequiresAdminAuthorization(path));

    [Fact]
    public void EveryUnsafeApiRequestRequiresAnExactOriginBeforeAuthentication()
    {
        var missing = LocalRequest("POST", "/api/v1/auth/login", origin: null);
        var crossOrigin = LocalRequest("PATCH", "/api/v1/clipboard/policy", "http://evil.invalid");
        var wrongScheme = LocalRequest("DELETE", "/api/v1/peers/peer-a", "https://127.0.0.1:5616");
        var exact = LocalRequest("PUT", "/api/v1/audio", "http://127.0.0.1:5616");
        var safeRead = LocalRequest("GET", "/api/v1/status", origin: null);

        Assert.Equal(StatusCodes.Status403Forbidden, ApiEndpoints.UnsafeApiOriginRejectionStatus(missing));
        Assert.Equal(StatusCodes.Status403Forbidden, ApiEndpoints.UnsafeApiOriginRejectionStatus(crossOrigin));
        Assert.Equal(StatusCodes.Status403Forbidden, ApiEndpoints.UnsafeApiOriginRejectionStatus(wrongScheme));
        Assert.Null(ApiEndpoints.UnsafeApiOriginRejectionStatus(exact));
        Assert.Null(ApiEndpoints.UnsafeApiOriginRejectionStatus(safeRead));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/assets/lanswitch-console.js")]
    [InlineData("/assets/lanswitch-console.css")]
    [InlineData("/devices")]
    public void LocalConsoleResourcesCannotReuseAnOlderCachedBundle(string path)
    {
        var context = new DefaultHttpContext();

        ApiEndpoints.ApplyLocalCachePolicy(context.Response, new PathString(path));

        Assert.Equal("no-cache, no-store, must-revalidate", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.Equal("0", context.Response.Headers.Expires);
    }

    [Theory]
    [InlineData("/api/v1/auth/status")]
    [InlineData("/api/v1/status")]
    public void LocalApiResponsesRemainNonCacheable(string path)
    {
        var context = new DefaultHttpContext();

        ApiEndpoints.ApplyLocalCachePolicy(context.Response, new PathString(path));

        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.False(context.Response.Headers.ContainsKey("Pragma"));
        Assert.False(context.Response.Headers.ContainsKey("Expires"));
    }

    private static HttpRequest LocalRequest(string method, string path, string? origin)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 5616);
        context.Request.Path = path;
        if (origin is not null) context.Request.Headers.Origin = origin;
        return context.Request;
    }

    private sealed class TestContext : IDisposable
    {
        private TestContext(
            string directory,
            AgentOptions options,
            ManualTimeProvider time,
            LocalAdminService service,
            TimeSpan idle,
            TimeSpan absolute,
            TimeSpan rotation)
        {
            Directory = directory;
            Options = options;
            Time = time;
            Service = service;
            Idle = idle;
            Absolute = absolute;
            Rotation = rotation;
        }

        internal string Directory { get; }
        internal AgentOptions Options { get; }
        internal ManualTimeProvider Time { get; }
        internal LocalAdminService Service { get; }
        internal TimeSpan Idle { get; }
        internal TimeSpan Absolute { get; }
        internal TimeSpan Rotation { get; }
        internal string CredentialPath => Path.Combine(Directory, AdminCredentialStore.FileName);
        internal string SettingsSentinelPath => Path.Combine(Directory, "settings.json");
        internal string IdentitySentinelPath => Path.Combine(Directory, "identity.bin");

        internal static TestContext Create(
            TimeSpan? idle = null,
            TimeSpan? absolute = null,
            TimeSpan? rotation = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"DeskMesh-admin-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var options = AgentOptions.Parse(["--data-dir", directory]);
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero));
            var selectedIdle = idle ?? LocalAdminService.IdleLifetime;
            var selectedAbsolute = absolute ?? LocalAdminService.AbsoluteLifetime;
            var selectedRotation = rotation ?? LocalAdminService.RotationInterval;
            var service = new LocalAdminService(
                options,
                time,
                LocalAdminService.MinimumKdfIterations,
                selectedIdle,
                selectedAbsolute,
                selectedRotation,
                LocalAdminService.RotationGrace);
            return new TestContext(directory, options, time, service,
                selectedIdle, selectedAbsolute, selectedRotation);
        }

        internal LocalAdminService CreateService() => new(
            Options,
            Time,
            LocalAdminService.MinimumKdfIterations,
            Idle,
            Absolute,
            Rotation,
            LocalAdminService.RotationGrace);

        public void Dispose()
        {
            foreach (var path in new[]
                     {
                         CredentialPath,
                         CredentialPath + ".new",
                         SettingsSentinelPath,
                         IdentitySentinelPath
                     })
                if (File.Exists(path)) File.Delete(path);
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: false);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan value) => _now += value;
    }
}
