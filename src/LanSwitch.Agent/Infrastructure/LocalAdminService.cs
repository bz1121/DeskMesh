using System.Security.Cryptography;
using System.Text;

namespace LanSwitch.Agent.Infrastructure;

internal sealed class LocalAdminService
{
    internal const string ContextItemName = "DeskMesh.AdminSession";
    internal const string PasswordAlgorithm = "PBKDF2-SHA512";
    internal const int DefaultKdfIterations = 310_000;
    internal const int MinimumKdfIterations = 100_000;
    internal const int MaximumKdfIterations = 2_000_000;
    internal const int MinimumUsernameLength = 3;
    internal const int MaximumUsernameLength = 32;
    internal const int MinimumPasswordLength = 6;
    internal const int MaximumPasswordLength = 256;
    internal const int MaximumFailures = 5;
    internal const int MaximumActiveSessions = 16;

    internal static readonly TimeSpan BootstrapLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(2);
    internal static readonly TimeSpan RotationInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private readonly AdminCredentialStore _credentialStore;
    private readonly AppState? _appState;
    private readonly TimeProvider _timeProvider;
    private readonly int _kdfIterations;
    private readonly TimeSpan _idleLifetime;
    private readonly TimeSpan _absoluteLifetime;
    private readonly TimeSpan _rotationInterval;
    private readonly TimeSpan _rotationGrace;
    private readonly object _credentialStateGate = new();
    private readonly object _sessionGate = new();
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly Dictionary<string, SessionTokenBinding> _tokensByHash = new(StringComparer.Ordinal);
    private AdminCredentialState _credentialState;
    private StoredAdminCredential? _credential;
    private BootstrapSecret? _bootstrap;
    private bool _lastSessionEndNotified = true;

    public LocalAdminService(AgentOptions options, AppState appState) : this(
        options,
        TimeProvider.System,
        DefaultKdfIterations,
        IdleLifetime,
        AbsoluteLifetime,
        RotationInterval,
        RotationGrace)
    {
        _appState = appState;
        if (CredentialState == AdminCredentialState.RecoveryRequired)
            AddDiagnostic("error", "管理员凭据无法读取；控制台已保持锁定，必须从本机托盘重置登录。");
    }

    internal LocalAdminService(
        AgentOptions options,
        TimeProvider timeProvider,
        int kdfIterations = DefaultKdfIterations,
        TimeSpan? idleLifetime = null,
        TimeSpan? absoluteLifetime = null,
        TimeSpan? rotationInterval = null,
        TimeSpan? rotationGrace = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (kdfIterations is < MinimumKdfIterations or > MaximumKdfIterations)
            throw new ArgumentOutOfRangeException(nameof(kdfIterations));

        _timeProvider = timeProvider;
        _kdfIterations = kdfIterations;
        _idleLifetime = idleLifetime ?? IdleLifetime;
        _absoluteLifetime = absoluteLifetime ?? AbsoluteLifetime;
        _rotationInterval = rotationInterval ?? RotationInterval;
        _rotationGrace = rotationGrace ?? RotationGrace;
        if (_idleLifetime <= TimeSpan.Zero || _absoluteLifetime < _idleLifetime ||
            _rotationInterval <= TimeSpan.Zero || _rotationGrace < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleLifetime));

        _credentialStore = new AdminCredentialStore(options.DataDirectory);
        CookieName = "DeskMesh.Admin." + InstanceHash(options);
        var loaded = _credentialStore.Load();
        if (loaded.State == AdminCredentialState.Ready && IsUsableCredential(loaded.Credential))
        {
            _credentialState = AdminCredentialState.Ready;
            _credential = loaded.Credential;
        }
        else
        {
            _credentialState = loaded.State == AdminCredentialState.Missing
                ? AdminCredentialState.Missing
                : AdminCredentialState.RecoveryRequired;
        }
    }

    internal string CookieName { get; }
    internal string CredentialPath => _credentialStore.Path;
    internal event Action? LastSessionEnded;
    internal AdminCredentialState CredentialState
    {
        get { lock (_credentialStateGate) return _credentialState; }
    }

    internal AdminBootstrapGrant IssueBootstrapToken()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_credentialStateGate)
        {
            if (_credentialState == AdminCredentialState.RecoveryRequired)
                throw new InvalidOperationException("Administrator credential recovery is required.");
            if (_credentialState == AdminCredentialState.Ready)
                throw new InvalidOperationException("Administrator setup has already been completed.");

            ClearBootstrap();
            var token = CreateToken();
            _bootstrap = new BootstrapSecret(HashSecret(token), now + BootstrapLifetime);
            return new AdminBootstrapGrant(token, _bootstrap.ExpiresAt);
        }
    }

    internal AdminAuthenticationResult Authenticate(
        string? token,
        bool allowRotation = true,
        bool extendIdle = false)
    {
        var state = CredentialState;
        if (state == AdminCredentialState.Missing) return AdminAuthenticationResult.SetupRequired();
        if (state == AdminCredentialState.RecoveryRequired) return AdminAuthenticationResult.ForRecovery();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
            return AdminAuthenticationResult.Unauthenticated();

        var tokenHash = HashSecretString(token);
        var now = _timeProvider.GetUtcNow();
        lock (_sessionGate)
        {
            PruneSessions(now);
            if (!_tokensByHash.TryGetValue(tokenHash, out var binding) ||
                binding.ValidUntil is { } validUntil && validUntil <= now ||
                binding.Session.Revocation.IsCancellationRequested)
                return AdminAuthenticationResult.Unauthenticated();

            var session = binding.Session;
            if (session.AbsoluteExpiresAt <= now || session.IdleExpiresAt <= now)
            {
                RevokeSession(session);
                return AdminAuthenticationResult.Unauthenticated();
            }

            string? cookieToken = null;
            if (extendIdle)
            {
                session.IdleExpiresAt = Min(now + _idleLifetime, session.AbsoluteExpiresAt);
                session.ScheduleExpiration(now);
                cookieToken = token;
            }

            if (string.Equals(tokenHash, session.CurrentTokenHash, StringComparison.Ordinal) &&
                allowRotation && now - session.RotatedAt >= _rotationInterval)
            {
                var replacement = CreateToken();
                var replacementHash = HashSecretString(replacement);
                binding.ValidUntil = now + _rotationGrace;
                session.CurrentTokenHash = replacementHash;
                session.RotatedAt = now;
                _tokensByHash[replacementHash] = new SessionTokenBinding(session, null);
                cookieToken = replacement;
            }

            return AdminAuthenticationResult.Authenticated(
                session.Id,
                session.Username,
                session.IdleExpiresAt,
                session.AbsoluteExpiresAt,
                cookieToken,
                session.CsrfToken,
                session.Revocation.Token);
        }
    }

    internal bool MatchesCsrf(AdminAuthenticationResult authentication, string? suppliedToken) =>
        authentication.IsAuthenticated &&
        !string.IsNullOrWhiteSpace(authentication.CsrfToken) &&
        !string.IsNullOrWhiteSpace(suppliedToken) &&
        suppliedToken.Length <= 128 &&
        FixedTimeSecretEquals(authentication.CsrfToken, suppliedToken);

    internal async Task<AdminLoginResult> SetupAsync(
        string? username,
        string? password,
        string? bootstrapToken,
        CancellationToken cancellationToken = default)
    {
        var validatedUsername = ValidateUsername(username);
        ValidatePassword(password);
        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            var state = CredentialState;
            if (state == AdminCredentialState.RecoveryRequired) return AdminLoginResult.RecoveryRequired();
            if (state == AdminCredentialState.Ready)
                return AdminLoginResult.Conflict("Administrator setup has already been completed.");
            if (!ConsumeBootstrapToken(bootstrapToken)) return AdminLoginResult.InvalidBootstrap();

            var now = _timeProvider.GetUtcNow();
            var credential = CreateCredential(validatedUsername, password!, now, now, now);
            try
            {
                await _credentialStore.WriteAsync(credential, cancellationToken);
            }
            catch
            {
                ApplyLoadResult(_credentialStore.Load());
                throw;
            }
            SetCredential(AdminCredentialState.Ready, credential);
            lock (_sessionGate)
            {
                RevokeAllSessions();
                var result = CreateSession(validatedUsername, now);
                AddDiagnostic("success", "管理员初始化已完成。");
                return result;
            }
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    internal async Task<AdminLoginResult> LoginAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var state = CredentialState;
        if (state == AdminCredentialState.Missing) return AdminLoginResult.SetupRequired();
        if (state == AdminCredentialState.RecoveryRequired) return AdminLoginResult.RecoveryRequired();

        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            await _credentialGate.WaitAsync(cancellationToken);
            try
            {
                var credential = CredentialSnapshot();
                if (credential is null) return AdminLoginResult.RecoveryRequired();
                var now = _timeProvider.GetUtcNow();
                if (credential.LockedUntil is { } lockedUntil && lockedUntil > now)
                {
                    AddDiagnostic("warning", "管理员登录因连续失败仍处于锁定期。");
                    return AdminLoginResult.Locked(lockedUntil);
                }

                var inputsBounded = username is not null && password is not null &&
                    username.Length <= MaximumUsernameLength * 4 && password.Length <= MaximumPasswordLength;
                var usernameMatches = inputsBounded && FixedTimeSecretEquals(
                    NormalizeUsernameForComparison(username!),
                    NormalizeUsernameForComparison(credential.Username));
                var passwordMatches = inputsBounded && VerifyPassword(password!, credential);
                if (!usernameMatches || !passwordMatches)
                    return await RecordFailedLoginAsync(credential, now, cancellationToken);

                var updated = credential with
                {
                    LastLoginAt = now,
                    FailedAttempts = 0,
                    FailureWindowStartedAt = null,
                    LockedUntil = null,
                    UpdatedAt = now
                };
                await _credentialStore.WriteAsync(updated, cancellationToken);
                SetCredential(AdminCredentialState.Ready, updated);
                lock (_sessionGate)
                {
                    var result = CreateSession(updated.Username, now);
                    AddDiagnostic("success", "管理员登录成功。");
                    return result;
                }
            }
            finally
            {
                _credentialGate.Release();
            }
        }
        finally
        {
            _loginGate.Release();
        }
    }

    internal async Task<AdminLoginResult> ChangePasswordAsync(
        string sessionId,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(newPassword);
        if (currentPassword is null || currentPassword.Length > MaximumPasswordLength)
            return AdminLoginResult.InvalidCredentials();

        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            var credential = CredentialSnapshot();
            if (credential is null || !VerifyPassword(currentPassword, credential))
                return AdminLoginResult.InvalidCredentials();
            lock (_sessionGate)
            {
                PruneSessions(_timeProvider.GetUtcNow());
                if (!_tokensByHash.Values.Any(value => value.Session.Id == sessionId))
                    return AdminLoginResult.InvalidCredentials();
            }

            var now = _timeProvider.GetUtcNow();
            var replacement = CreateCredential(
                credential.Username,
                newPassword!,
                credential.CreatedAt,
                now,
                now);
            await _credentialStore.WriteAsync(replacement, cancellationToken);
            SetCredential(AdminCredentialState.Ready, replacement);
            lock (_sessionGate)
            {
                RevokeAllSessions();
                var result = CreateSession(credential.Username, now);
                AddDiagnostic("success", "管理员密码已更改，旧会话已全部撤销。");
                return result;
            }
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    internal void Logout(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return;
        lock (_sessionGate)
        {
            if (_tokensByHash.TryGetValue(HashSecretString(token), out var binding))
            {
                RevokeSession(binding.Session);
                AddDiagnostic("info", "管理员已退出登录，会话已撤销。");
            }
        }
    }

    internal int RevokeOtherSessions(string currentSessionId, bool includeCurrent)
    {
        lock (_sessionGate)
        {
            PruneSessions(_timeProvider.GetUtcNow());
            var currentIsActive = _tokensByHash.Values.Any(value =>
                value.Session.Id == currentSessionId &&
                !value.Session.Revocation.IsCancellationRequested);
            if (!currentIsActive)
                throw new UnauthorizedAccessException("Administrator session is no longer active.");
            var sessions = _tokensByHash.Values.Select(static value => value.Session).Distinct()
                .Where(value => includeCurrent || value.Id != currentSessionId).ToArray();
            foreach (var session in sessions) RevokeSession(session);
            AddDiagnostic("info", $"管理员撤销了 {sessions.Length} 个会话。");
            return sessions.Length;
        }
    }

    internal async Task ResetCredentialAsync(CancellationToken cancellationToken = default)
    {
        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            lock (_sessionGate) RevokeAllSessions();
            try
            {
                await _credentialStore.ResetAsync(cancellationToken);
                SetCredential(AdminCredentialState.Missing, null);
                lock (_credentialStateGate) ClearBootstrap();
                AddDiagnostic("warning", "管理员凭据已从本机托盘重置；设备身份、配对和其他设置保持不变。");
            }
            catch
            {
                ApplyLoadResult(_credentialStore.Load());
                throw;
            }
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    internal Task ResetAsync(CancellationToken cancellationToken = default) => ResetCredentialAsync(cancellationToken);

    internal AdminStatusView GetStatus(string? token) => GetStatus(Authenticate(token));

    internal AdminStatusView GetStatus(AdminAuthenticationResult authentication)
    {
        var state = CredentialState;
        var credential = CredentialSnapshot();
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? lockedUntil = credential?.LockedUntil is { } locked && locked > now ? locked : null;
        var failedAttempts = credential is not null &&
            credential.FailureWindowStartedAt is { } start && now - start < FailureWindow
                ? credential.FailedAttempts
                : 0;
        int? sessionCount = null;
        if (authentication.IsAuthenticated)
        {
            lock (_sessionGate)
            {
                PruneSessions(now);
                sessionCount = _tokensByHash.Values.Select(static value => value.Session).Distinct().Count();
            }
        }
        var stateLabel = state switch
        {
            AdminCredentialState.Missing => "setup-required",
            AdminCredentialState.RecoveryRequired => "recovery-required",
            _ when authentication.IsAuthenticated => "authenticated",
            _ => "login-required"
        };
        return new AdminStatusView(
            stateLabel,
            state == AdminCredentialState.Ready,
            state == AdminCredentialState.RecoveryRequired,
            authentication.IsAuthenticated,
            authentication.IsAuthenticated ? authentication.Username : null,
            authentication.IsAuthenticated ? "administrator" : null,
            authentication.AbsoluteExpiresAt,
            authentication.ExpiresAt,
            sessionCount,
            failedAttempts,
            lockedUntil,
            authentication.IsAuthenticated ? credential?.LastLoginAt : null);
    }

    internal void AppendSessionCookie(HttpResponse response, string token, DateTimeOffset expiresAt)
    {
        response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            Path = "/",
            Expires = expiresAt
        });
        response.Headers.CacheControl = "no-store";
    }

    internal void DeleteSessionCookie(HttpResponse response)
    {
        response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            Path = "/"
        });
        response.Headers.CacheControl = "no-store";
    }

    private async Task<AdminLoginResult> RecordFailedLoginAsync(
        StoredAdminCredential credential,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var insideWindow = credential.FailureWindowStartedAt is { } start && now - start < FailureWindow;
        var attempts = insideWindow ? credential.FailedAttempts + 1 : 1;
        DateTimeOffset? lockedUntil = attempts >= MaximumFailures ? now + LockoutDuration : null;
        var updated = credential with
        {
            FailedAttempts = Math.Min(attempts, MaximumFailures),
            FailureWindowStartedAt = insideWindow ? credential.FailureWindowStartedAt : now,
            LockedUntil = lockedUntil,
            UpdatedAt = now
        };
        await _credentialStore.WriteAsync(updated, cancellationToken);
        SetCredential(AdminCredentialState.Ready, updated);
        if (lockedUntil is not null)
        {
            AddDiagnostic("warning", "管理员登录连续失败，已临时锁定。");
            return AdminLoginResult.Locked(lockedUntil.Value);
        }
        AddDiagnostic("warning", $"管理员登录失败；剩余尝试次数 {MaximumFailures - attempts}。");
        return AdminLoginResult.InvalidCredentials(MaximumFailures - attempts);
    }

    private bool ConsumeBootstrapToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return false;
        var candidate = HashSecret(token);
        try
        {
            lock (_credentialStateGate)
            {
                if (_bootstrap is null || _bootstrap.ExpiresAt <= _timeProvider.GetUtcNow())
                {
                    ClearBootstrap();
                    return false;
                }
                if (!CryptographicOperations.FixedTimeEquals(candidate, _bootstrap.TokenHash)) return false;
                ClearBootstrap();
                return true;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    private void ClearBootstrap()
    {
        if (_bootstrap is not null) CryptographicOperations.ZeroMemory(_bootstrap.TokenHash);
        _bootstrap = null;
    }

    private AdminLoginResult CreateSession(string username, DateTimeOffset now)
    {
        PruneSessions(now);
        while (true)
        {
            var sessions = _tokensByHash.Values.Select(static value => value.Session).Distinct().ToArray();
            if (sessions.Length < MaximumActiveSessions) break;
            var oldest = sessions.OrderBy(static value => value.CreatedAt)
                .ThenBy(static value => value.Id, StringComparer.Ordinal)
                .First();
            RevokeSession(oldest);
        }
        var token = CreateToken();
        var tokenHash = HashSecretString(token);
        var absoluteExpiresAt = now + _absoluteLifetime;
        var idleExpiresAt = Min(now + _idleLifetime, absoluteExpiresAt);
        var session = new AdminSession(
            Guid.NewGuid().ToString("N"),
            username,
            tokenHash,
            CreateToken(),
            now,
            now,
            idleExpiresAt,
            absoluteExpiresAt);
        _tokensByHash[tokenHash] = new SessionTokenBinding(session, null);
        _lastSessionEndNotified = false;
        session.Revocation.Token.Register(static state =>
        {
            var service = (LocalAdminService)state!;
            ThreadPool.UnsafeQueueUserWorkItem(
                static candidate => candidate.NotifyLastSessionEndedIfNeeded(),
                service,
                preferLocal: false);
        }, this);
        session.ScheduleExpiration(now);
        return AdminLoginResult.Success(session.Id, username, token, idleExpiresAt, absoluteExpiresAt);
    }

    private void NotifyLastSessionEndedIfNeeded()
    {
        var shouldNotify = false;
        lock (_sessionGate)
        {
            var now = _timeProvider.GetUtcNow();
            var hasActiveSession = _tokensByHash.Values
                .Select(static value => value.Session)
                .Distinct()
                .Any(value => !value.Revocation.IsCancellationRequested &&
                    value.IdleExpiresAt > now && value.AbsoluteExpiresAt > now);
            if (!hasActiveSession && !_lastSessionEndNotified)
            {
                _lastSessionEndNotified = true;
                shouldNotify = true;
            }
        }

        if (!shouldNotify) return;
        try { LastSessionEnded?.Invoke(); }
        catch (Exception exception)
        {
            AddDiagnostic("error", $"管理员会话结束后的安全回切失败：{exception.Message}");
        }
    }

    private void PruneSessions(DateTimeOffset now)
    {
        foreach (var session in _tokensByHash.Values.Select(static value => value.Session).Distinct()
                     .Where(value => value.Revocation.IsCancellationRequested ||
                         value.IdleExpiresAt <= now || value.AbsoluteExpiresAt <= now).ToArray())
            RevokeSession(session);
        foreach (var hash in _tokensByHash.Where(value => value.Value.ValidUntil is { } until && until <= now)
                     .Select(static value => value.Key).ToArray())
            _tokensByHash.Remove(hash);
    }

    private void RevokeAllSessions()
    {
        foreach (var session in _tokensByHash.Values.Select(static value => value.Session).Distinct().ToArray())
            RevokeSession(session);
    }

    private void RevokeSession(AdminSession session)
    {
        if (!session.Revocation.IsCancellationRequested)
        {
            try { session.Revocation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        foreach (var hash in _tokensByHash.Where(value => ReferenceEquals(value.Value.Session, session))
                     .Select(static value => value.Key).ToArray())
            _tokensByHash.Remove(hash);
        // Do not dispose here: a request may have authenticated immediately before
        // revocation and still needs to register its linked cancellation callback.
        // The now-unreachable session and CTS are reclaimed together by the GC.
    }

    private StoredAdminCredential CreateCredential(
        string username,
        string password,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? lastLoginAt)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = DerivePassword(password, salt, _kdfIterations);
        try
        {
            return new StoredAdminCredential(
                username,
                PasswordAlgorithm,
                _kdfIterations,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(hash),
                createdAt,
                updatedAt,
                lastLoginAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static bool VerifyPassword(string password, StoredAdminCredential credential)
    {
        try
        {
            var salt = Convert.FromBase64String(credential.SaltBase64);
            var expected = Convert.FromBase64String(credential.PasswordHashBase64);
            var actual = DerivePassword(password, salt, credential.Iterations);
            try { return CryptographicOperations.FixedTimeEquals(actual, expected); }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(actual);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] DerivePassword(string password, byte[] salt, int iterations)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA512,
                32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static bool IsUsableCredential(StoredAdminCredential? credential)
    {
        if (credential is null || credential.Algorithm != PasswordAlgorithm ||
            credential.Iterations is < MinimumKdfIterations or > MaximumKdfIterations ||
            string.IsNullOrWhiteSpace(credential.Username) ||
            credential.Username.Length is < MinimumUsernameLength or > MaximumUsernameLength ||
            credential.Username.Any(char.IsControl) ||
            credential.FailedAttempts is < 0 or > MaximumFailures ||
            string.IsNullOrWhiteSpace(credential.SaltBase64) ||
            string.IsNullOrWhiteSpace(credential.PasswordHashBase64))
            return false;
        try
        {
            return Convert.FromBase64String(credential.SaltBase64).Length == 32 &&
                Convert.FromBase64String(credential.PasswordHashBase64).Length == 32;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ValidateUsername(string? username)
    {
        var value = string.IsNullOrWhiteSpace(username)
            ? "admin"
            : username.Trim().Normalize(NormalizationForm.FormKC);
        if (value.Length is < MinimumUsernameLength or > MaximumUsernameLength || value.Any(char.IsControl))
            throw new ArgumentException(
                $"Administrator name must contain {MinimumUsernameLength}-{MaximumUsernameLength} printable characters.",
                nameof(username));
        return value;
    }

    private static void ValidatePassword(string? password)
    {
        if (password is null || password.Length is < MinimumPasswordLength or > MaximumPasswordLength)
            throw new ArgumentException(
                $"Password must contain {MinimumPasswordLength}-{MaximumPasswordLength} characters.",
                nameof(password));
    }

    private static string NormalizeUsernameForComparison(string username) =>
        username.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private StoredAdminCredential? CredentialSnapshot()
    {
        lock (_credentialStateGate) return _credential;
    }

    private void SetCredential(AdminCredentialState state, StoredAdminCredential? credential)
    {
        lock (_credentialStateGate)
        {
            _credentialState = state;
            _credential = credential;
        }
    }

    private void ApplyLoadResult(AdminCredentialLoadResult loaded)
    {
        if (loaded.State == AdminCredentialState.Ready && IsUsableCredential(loaded.Credential))
            SetCredential(AdminCredentialState.Ready, loaded.Credential);
        else
            SetCredential(
                loaded.State == AdminCredentialState.Missing
                    ? AdminCredentialState.Missing
                    : AdminCredentialState.RecoveryRequired,
                null);
    }

    private void AddDiagnostic(string level, string message) =>
        _appState?.AddDiagnostic(level, "管理员登录", message);

    private static string InstanceHash(AgentOptions options)
    {
        var material = $"{options.DataDirectory.ToUpperInvariant()}\0{options.InstanceName}\0{options.WebPort}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..12].ToLowerInvariant();
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] HashSecret(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string HashSecretString(string value) => Convert.ToHexString(HashSecret(value));

    private static bool FixedTimeSecretEquals(string first, string second)
    {
        var firstHash = HashSecret(first);
        var secondHash = HashSecret(second);
        try { return CryptographicOperations.FixedTimeEquals(firstHash, secondHash); }
        finally
        {
            CryptographicOperations.ZeroMemory(firstHash);
            CryptographicOperations.ZeroMemory(secondHash);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first <= second ? first : second;

    private sealed record BootstrapSecret(byte[] TokenHash, DateTimeOffset ExpiresAt);

    private sealed class AdminSession(
        string id,
        string username,
        string currentTokenHash,
        string csrfToken,
        DateTimeOffset createdAt,
        DateTimeOffset rotatedAt,
        DateTimeOffset idleExpiresAt,
        DateTimeOffset absoluteExpiresAt)
    {
        internal string Id { get; } = id;
        internal string Username { get; } = username;
        internal string CurrentTokenHash { get; set; } = currentTokenHash;
        internal string CsrfToken { get; } = csrfToken;
        internal DateTimeOffset CreatedAt { get; } = createdAt;
        internal DateTimeOffset RotatedAt { get; set; } = rotatedAt;
        internal DateTimeOffset IdleExpiresAt { get; set; } = idleExpiresAt;
        internal DateTimeOffset AbsoluteExpiresAt { get; } = absoluteExpiresAt;
        internal CancellationTokenSource Revocation { get; } = new();

        internal void ScheduleExpiration(DateTimeOffset now)
        {
            var expiresAt = IdleExpiresAt <= AbsoluteExpiresAt ? IdleExpiresAt : AbsoluteExpiresAt;
            Revocation.CancelAfter(expiresAt <= now ? TimeSpan.Zero : expiresAt - now);
        }
    }

    private sealed class SessionTokenBinding(AdminSession session, DateTimeOffset? validUntil)
    {
        internal AdminSession Session { get; } = session;
        internal DateTimeOffset? ValidUntil { get; set; } = validUntil;
    }
}

internal sealed record AdminBootstrapGrant(string Token, DateTimeOffset ExpiresAt);

internal sealed record AdminAuthenticationResult(
    AdminCredentialState CredentialState,
    bool IsAuthenticated,
    string? SessionId,
    string? Username,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AbsoluteExpiresAt,
    string? ReplacementToken,
    string? CsrfToken,
    CancellationToken RevocationToken)
{
    internal bool IsConfigured => CredentialState == AdminCredentialState.Ready;
    internal bool RecoveryRequired => CredentialState == AdminCredentialState.RecoveryRequired;
    internal static AdminAuthenticationResult SetupRequired() =>
        new(AdminCredentialState.Missing, false, null, null, null, null, null, null, CancellationToken.None);
    internal static AdminAuthenticationResult ForRecovery() =>
        new(AdminCredentialState.RecoveryRequired, false, null, null, null, null, null, null, CancellationToken.None);
    internal static AdminAuthenticationResult Unauthenticated() =>
        new(AdminCredentialState.Ready, false, null, null, null, null, null, null, CancellationToken.None);
    internal static AdminAuthenticationResult Authenticated(
        string sessionId,
        string username,
        DateTimeOffset expiresAt,
        DateTimeOffset absoluteExpiresAt,
        string? replacementToken,
        string csrfToken,
        CancellationToken revocationToken) =>
        new(AdminCredentialState.Ready, true, sessionId, username, expiresAt, absoluteExpiresAt,
            replacementToken, csrfToken, revocationToken);
}

internal sealed record AdminLoginResult(
    string Status,
    string? SessionId,
    string? Username,
    string? Token,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AbsoluteExpiresAt,
    DateTimeOffset? LockedUntil,
    int? AttemptsRemaining,
    string? Error)
{
    internal bool Succeeded => Status == "authenticated";
    internal static AdminLoginResult Success(string id, string username, string token, DateTimeOffset expires, DateTimeOffset absolute) =>
        new("authenticated", id, username, token, expires, absolute, null, null, null);
    internal static AdminLoginResult SetupRequired() =>
        new("setup-required", null, null, null, null, null, null, null, "Administrator setup is required.");
    internal static AdminLoginResult RecoveryRequired() =>
        new("recovery-required", null, null, null, null, null, null, null, "Administrator credential recovery is required.");
    internal static AdminLoginResult Conflict(string error) =>
        new("conflict", null, null, null, null, null, null, null, error);
    internal static AdminLoginResult InvalidBootstrap() =>
        new("invalid-bootstrap-token", null, null, null, null, null, null, null, "Bootstrap token is invalid or expired.");
    internal static AdminLoginResult InvalidCredentials(int? remaining = null) =>
        new("invalid-credentials", null, null, null, null, null, null, remaining, "Invalid administrator name or password.");
    internal static AdminLoginResult Locked(DateTimeOffset until) =>
        new("locked", null, null, null, null, null, until, 0, "Too many failed login attempts.");
}

internal sealed record AdminStatusView(
    string State,
    bool Configured,
    bool RecoveryRequired,
    bool Authenticated,
    string? Username,
    string? Role,
    DateTimeOffset? SessionExpiresAt,
    DateTimeOffset? IdleExpiresAt,
    int? ActiveSessionCount,
    int FailedAttempts,
    DateTimeOffset? LockedUntil,
    DateTimeOffset? LastLoginAt);
