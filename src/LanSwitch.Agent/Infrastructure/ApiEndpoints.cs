using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LanSwitch.Agent.Services;
using LanSwitch.Core.Protocol;
using LanSwitch.Windows.Clipboard;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace LanSwitch.Agent.Infrastructure;

public static class ApiEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseExceptionHandler(exceptionApp => exceptionApp.Run(async context =>
        {
            var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
            var error = feature?.Error ?? new Exception("Unknown server error.");
            context.RequestServices.GetService<ILoggerFactory>()?
                .CreateLogger("DeskMesh.Api")
                .LogError(error, "Unhandled API exception. TraceId={TraceId}", context.TraceIdentifier);
            var classification = ClassifyException(
                error,
                context.RequestAborted.IsCancellationRequested);
            context.Response.StatusCode = classification.StatusCode;
            if (classification.Detail is not null)
                await Results.Problem(classification.Detail, statusCode: classification.StatusCode).ExecuteAsync(context);
        }));
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            KeepAliveTimeout = Timeout.InfiniteTimeSpan
        });
        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<AgentOptions>();
            var path = context.Request.Path;
            var requestLimit = RequestLimit(path);
            var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = requestLimit;
            if (context.Request.ContentLength is > 0 && context.Request.ContentLength > requestLimit)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            if (context.Connection.LocalPort == options.WebPort)
            {
                ApplyLocalSecurityHeaders(context.Response, path);
                if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) ||
                    !IsLoopbackHost(context.Request.Host.Host))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (path.StartsWithSegments("/api") && !HasValidLocalOrigin(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/v1") &&
                    IsUnsafe(context.Request.Method) &&
                    !path.StartsWithSegments("/api/v1/session"))
                {
                    if (!HasExactLocalOrigin(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new { error = "请求来源或本地会话令牌无效，请刷新页面。" });
                        return;
                    }
                }
                if (UnsafeApiOriginRejectionStatus(context.Request) is { } originRejectionStatus)
                {
                    context.Response.StatusCode = originRejectionStatus;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "An exact same-origin browser request is required.",
                        code = "invalid-origin"
                    });
                    return;
                }
                if (path.StartsWithSegments("/api/v1") && RequiresAdminAuthorization(path))
                {
                    var admin = context.RequestServices.GetRequiredService<LocalAdminService>();
                    var unsafeRequest = IsUnsafe(context.Request.Method);
                    var cookieToken = context.Request.Cookies[admin.CookieName];
                    var authentication = admin.Authenticate(
                        cookieToken,
                        allowRotation: !unsafeRequest,
                        extendIdle: false);
                    if (authentication.RecoveryRequired)
                    {
                        context.Response.StatusCode = StatusCodes.Status423Locked;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "Administrator credential recovery is required.",
                            code = "credential-recovery-required"
                        });
                        return;
                    }
                    if (!authentication.IsConfigured)
                    {
                        context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "Administrator setup is required.",
                            code = "administrator-setup-required"
                        });
                        return;
                    }
                    if (!authentication.IsAuthenticated)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "Administrator login is required.",
                            code = "administrator-login-required"
                        });
                        return;
                    }
                    if (unsafeRequest &&
                        !admin.MatchesCsrf(authentication, context.Request.Headers["X-LanSwitch-Client"]))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "The administrator session CSRF token is invalid.",
                            code = "invalid-session-csrf"
                        });
                        return;
                    }
                    if (unsafeRequest)
                    {
                        authentication = admin.Authenticate(cookieToken, allowRotation: true, extendIdle: true);
                        if (!authentication.IsAuthenticated)
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }
                    }
                    context.Items[LocalAdminService.ContextItemName] = authentication;
                    if (authentication.ReplacementToken is not null && authentication.ExpiresAt is { } expiresAt)
                        admin.AppendSessionCookie(context.Response, authentication.ReplacementToken, expiresAt);
                }
                if (path.StartsWithSegments("/peer"))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }
            else if (context.Connection.LocalPort == options.PeerPort)
            {
                if (!path.StartsWithSegments("/peer") || !PeerDiscoveryService.IsPrivate(context.Connection.RemoteIpAddress ?? IPAddress.None))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                if (!IsUnpairedPeerPath(path))
                {
                    var directory = context.RequestServices.GetRequiredService<PeerDirectory>();
                    var certificate = await context.Connection.GetClientCertificateAsync();
                    var fingerprint = certificate is null ? null : DeviceIdentityStore.Fingerprint(certificate);
                    var peer = fingerprint is null ? null : directory.FindByFingerprint(fingerprint);
                    if (peer is null)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                    context.Items["LanSwitch.Peer"] = peer;
                }
            }
            await next();
        });
        app.Use(async (context, next) =>
        {
            if (context.Items.TryGetValue(LocalAdminService.ContextItemName, out var value) &&
                value is AdminAuthenticationResult { IsAuthenticated: true } authentication &&
                !IsSessionRevocationEndpoint(context.Request.Path))
            {
                var original = context.RequestAborted;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    original,
                    authentication.RevocationToken);
                context.RequestAborted = linked.Token;
                try { await next(); }
                finally { context.RequestAborted = original; }
                return;
            }
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
    }

    public static void Map(WebApplication app)
    {
        MapLocalApi(app);
        MapPeerApi(app);
        app.MapFallbackToFile("index.html");
    }

    private static void MapLocalApi(WebApplication app)
    {
        app.MapGet("/api/v1/session", (HttpContext context, LocalAdminService admin) =>
        {
            var authentication = GetAdminAuthentication(context, admin);
            var auth = admin.GetStatus(authentication);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                token = authentication.IsAuthenticated ? authentication.CsrfToken : null,
                auth
            });
        });
        app.MapGet("/api/v1/auth/status", (HttpContext context, LocalAdminService admin) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(GetAdminStatus(context, admin));
        });
        app.MapPost("/api/v1/auth/setup", async (
            AdminSetupRequest request,
            HttpContext context,
            LocalAdminService admin,
            CancellationToken cancellationToken) =>
            AdminLoginResponse(
                context,
                admin,
                await admin.SetupAsync(
                    request.Username,
                    request.Password,
                    request.BootstrapToken,
                    cancellationToken)));
        app.MapPost("/api/v1/auth/login", async (
            AdminLoginRequest request,
            HttpContext context,
            LocalAdminService admin,
            CancellationToken cancellationToken) =>
            AdminLoginResponse(
                context,
                admin,
                await admin.LoginAsync(
                    request.Username,
                    request.Password,
                    cancellationToken)));
        app.MapPost("/api/v1/auth/logout", async (
            HttpContext context,
            LocalAdminService admin,
            FocusCoordinator focus) =>
        {
            await focus.RequestUserReleaseAsync(
                "管理员锁定控制台，已恢复本机控制。",
                "administrator-logout",
                CancellationToken.None);
            admin.Logout(context.Request.Cookies[admin.CookieName]);
            admin.DeleteSessionCookie(context.Response);
            return Results.Ok(admin.GetStatus((string?)null));
        });
        app.MapPost("/api/v1/auth/touch", (HttpContext context, LocalAdminService admin) =>
            Results.Ok(admin.GetStatus(RequireAdminSession(context))));
        app.MapPost("/api/v1/auth/password", async (
            AdminPasswordChangeRequest request,
            HttpContext context,
            LocalAdminService admin,
            CancellationToken cancellationToken) =>
        {
            var current = RequireAdminSession(context);
            return AdminLoginResponse(
                context,
                admin,
                await admin.ChangePasswordAsync(
                    current.SessionId!,
                    request.CurrentPassword,
                    request.NewPassword,
                    cancellationToken));
        });
        app.MapPost("/api/v1/auth/sessions/revoke", (
            AdminSessionRevokeRequest request,
            HttpContext context,
            LocalAdminService admin) =>
        {
            var current = RequireAdminSession(context);
            var count = admin.RevokeOtherSessions(current.SessionId!, request.IncludeCurrent);
            if (request.IncludeCurrent) admin.DeleteSessionCookie(context.Response);
            var auth = request.IncludeCurrent
                ? admin.GetStatus((string?)null)
                : GetAdminStatus(context, admin);
            return Results.Ok(new { revoked = count, auth });
        });
        app.MapGet("/api/v1/status", (AppState state, PeerDirectory peers) => Results.Ok(state.GetStatus(peers.HasPairedPeers)));
        app.MapGet("/api/v1/diagnostics", (AppState state) => Results.Ok(state.Diagnostics));
        app.MapDelete("/api/v1/diagnostics", (AppState state) =>
        {
            state.ClearDiagnostics();
            return Results.Ok(new { cleared = true });
        });
        app.MapGet("/api/v1/peers", (PeerDirectory peers) => Results.Ok(peers.Snapshot()));
        app.MapDelete("/api/v1/peers/{id}", async (string id, PeerDirectory peers, FocusCoordinator focus,
            InputCoordinator input, CancellationToken cancellationToken) =>
        {
            var removed = await peers.RemoveAsync(id, revoked =>
            {
                focus.EmergencyReleaseNow("设备信任已撤销，已恢复本机控制");
                input.RevokePeer(revoked.Id);
            }, cancellationToken);
            return Results.Ok(new { removed });
        });

        app.MapGet("/api/v1/security", (PairingService pairing, DeviceIdentity identity, SettingsStore settings) =>
        {
            var localCode = pairing.GetCurrentCode();
            return Results.Ok(new
            {
                pairingCode = localCode.PairingCode,
                expiresAt = localCode.ExpiresAt,
                fingerprint = identity.Fingerprint,
                transport = "mTLS / ECDSA P-256 / SHA-256 pinning",
                pending = pairing.Pending,
                emergencyHotkey = settings.Snapshot.EmergencyHotkey
            });
        });
        app.MapPost("/api/v1/security/pairing-code/refresh", (PairingService pairing) =>
            Results.Ok(pairing.RefreshCode()));
        app.MapPost("/api/v1/pairings", async (StartPairingRequest request, PairingService pairing, AgentOptions options,
            CancellationToken cancellationToken) =>
        {
            var (address, port) = ParseAddress(request.Address, options.PeerPort);
            var result = await pairing.StartOutgoingAsync(address, port, request.Code, cancellationToken);
            return Results.Accepted(value: result);
        });
        app.MapPost("/api/v1/pairings/{id}/confirm", async (string id, ConfirmPairingRequest request,
            PairingService pairing, CancellationToken cancellationToken) =>
            Results.Ok(await pairing.ConfirmIncomingAsync(id, request.Approve, cancellationToken)));

        app.MapPost("/api/v1/focus/switch", async (SwitchFocusRequest request, FocusCoordinator focus,
            SettingsStore settings, CancellationToken cancellationToken) =>
        {
            if (SwitchModeConfiguration.IsSeamlessRemote(settings.Snapshot.SwitchMode))
                return Results.Conflict(new
                {
                    error = "当前是无缝远程模式；请通过控制中心打开远程画面，或先切换为直接信号模式。"
                });
            return Results.Ok(await focus.SwitchAsync(request.TargetDeviceId, cancellationToken));
        });
        app.MapPost("/api/v1/focus/release", async (FocusCoordinator focus, CancellationToken cancellationToken) =>
            Results.Ok(await focus.RequestUserReleaseAsync(
                "网页请求切回本机",
                "web-api",
                cancellationToken)));
        app.MapGet("/api/v1/focus/mode", (SettingsStore settings) =>
            Results.Ok(new SwitchModeSettingsView(settings.Snapshot.SwitchMode)));
        app.MapPut("/api/v1/focus/mode", async (SwitchModeSettingsUpdateRequest request,
            FocusCoordinator focus, CancellationToken cancellationToken) =>
            Results.Ok(await focus.UpdateSwitchModeAsync(request.Mode, cancellationToken)));

        app.MapGet("/api/v1/clipboard", (ClipboardCoordinator clipboard) => Results.Ok(clipboard.GetPolicy()));
        app.MapPatch("/api/v1/clipboard/policy", async (ClipboardPolicyRequest request, ClipboardCoordinator clipboard,
            CancellationToken cancellationToken) => Results.Ok(await clipboard.UpdatePolicyAsync(request, cancellationToken)));
        app.MapDelete("/api/v1/clipboard", (ClipboardCoordinator clipboard) => { clipboard.ClearLatest(); return Results.NoContent(); });

        app.MapGet("/api/v1/files/offers", (FileTransferCoordinator files) => Results.Ok(files.Offers));
        app.MapPost("/api/v1/files/offers", async (HttpRequest request, FileTransferCoordinator files,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "请使用 multipart/form-data 上传。" });
            return Results.Accepted(value: await files.AcceptBrowserUploadAsync(request, cancellationToken));
        }).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(AgentOptions.MaxFileRequestBytes));
        app.MapPost("/api/v1/files/offers/{id}/decision", async (string id, FileDecisionRequest request,
            FileTransferCoordinator files, CancellationToken cancellationToken) =>
            Results.Ok(await files.DecideAsync(id, request.Accept, cancellationToken)));

        app.MapGet("/api/v1/displays", (DisplayCoordinator display) => Results.Ok(display.Snapshot()));
        app.MapPost("/api/v1/displays/probe", async (DisplayCoordinator display, CancellationToken cancellationToken) =>
            Results.Ok(await display.ProbeAsync(cancellationToken)));
        app.MapPut("/api/v1/displays/mapping", async (DisplayMappingRequest request, DisplayCoordinator display,
            CancellationToken cancellationToken) => Results.Ok(await display.SaveMappingAsync(request, cancellationToken)));
        app.MapPatch("/api/v1/displays/follow", async (DisplayFollowRequest request, DisplayCoordinator display,
            CancellationToken cancellationToken) => Results.Ok(await display.SetPhysicalFollowAsync(request.Enabled, cancellationToken)));
        app.MapPut("/api/v1/displays/compatibility", async (DisplayCompatibilityRequest request,
            DisplayCoordinator display, CancellationToken cancellationToken) =>
            Results.Ok(await display.SetWriteOnlyCompatibilityAsync(request, cancellationToken)));
        app.MapPost("/api/v1/displays/compatibility/test", async (DisplayCompatibilityTestRequest request,
            DisplayCoordinator display, CancellationToken cancellationToken) =>
        {
            var result = await display.TestWriteOnlyAsync(request, cancellationToken);
            return Results.Ok(new
            {
                accepted = result.CommandIssued,
                attempted = result.Attempted,
                commandIssued = result.CommandIssued,
                success = result.Verified,
                verified = result.Verified,
                compatibilityMode = result.CompatibilityMode,
                outcome = result.CommandIssued ? "command-sent-unverified" : "command-not-sent",
                confirmationId = result.ConfirmationId,
                message = result.Message,
                error = result.CommandIssued ? null : result.Message
            });
        });
        app.MapPost("/api/v1/displays/compatibility/test/confirm", async (
            DisplayCompatibilityTestConfirmation request, DisplayCoordinator display,
            CancellationToken cancellationToken) =>
            Results.Ok(await display.ConfirmWriteOnlyTestAsync(request, cancellationToken)));

        app.MapGet("/api/v1/events", HandleEventWebSocketAsync);
    }

    private static void MapPeerApi(WebApplication app)
    {
        app.MapGet("/peer/v1/hello", (DeviceIdentity identity, SettingsStore settings, AgentOptions options) => Results.Ok(new
        {
            protocol = ProtocolConstants.CurrentVersion,
            deviceId = identity.DeviceId,
            deviceName = settings.Snapshot.DeviceName,
            port = options.PeerPort,
            fingerprint = identity.Fingerprint,
            certificateBase64 = identity.CertificateBase64,
            capabilities = new[] { "clipboard", "files", "input", "display", "audio", "remote-desktop" }
        }));
        app.MapPost("/peer/v1/pairings", async (HttpContext context, IncomingPairRequest request, PairingService pairing) =>
        {
            var address = context.Connection.RemoteIpAddress?.ToString() ?? throw new InvalidOperationException("无法识别来源地址。");
            var certificate = await context.Connection.GetClientCertificateAsync()
                ?? throw new InvalidOperationException("配对请求缺少 TLS 客户端证书。");
            return Results.Accepted(value: pairing.Receive(request, address, request.Port, certificate));
        }).WithMetadata(new RequestSizeLimitAttribute(256 * 1024));
        app.MapGet("/peer/v1/pairings/{id}/status", (string id, PairingService pairing) => Results.Ok(pairing.GetRemoteStatus(id)));
        app.MapGet("/peer/v1/heartbeat", () => Results.Ok(new { now = DateTimeOffset.UtcNow }));

        app.MapPost("/peer/v1/focus/prepare", (HttpContext context, FocusCommand command, DeviceIdentity identity,
            PeerDirectory peers, FocusCoordinator focus) =>
        {
            if (!ValidateFocusCommand(context, command, identity, peers))
                return Results.Conflict(new FocusPrepareResponse(false, command.Epoch, false,
                    "焦点请求的设备身份或目标不匹配。"));
            var result = focus.PrepareRemote(command);
            return result.Ready ? Results.Ok(result) : Results.Conflict(result);
        });
        app.MapPost("/peer/v1/focus/commit", (HttpContext context, FocusCommand command, DeviceIdentity identity,
            PeerDirectory peers, FocusCoordinator focus) =>
            ValidateFocusCommand(context, command, identity, peers) && focus.CommitRemote(command)
                ? Results.Ok(new { committed = true, epoch = command.Epoch })
                : Results.Conflict(new { committed = false }));
        app.MapPost("/peer/v1/focus/abort", (HttpContext context, FocusCommand command, DeviceIdentity identity,
            PeerDirectory peers, FocusCoordinator focus) =>
        {
            if (!ValidateFocusCommand(context, command, identity, peers)) return Results.Conflict(new { aborted = false });
            focus.AbortRemote(command);
            return Results.Ok(new { aborted = true });
        });
        app.MapPost("/peer/v1/display/switch", async (HttpContext context, PeerDisplaySwitchCommand command,
            PeerDirectory peers, FocusCoordinator focus) =>
        {
            var source = RequireAuthorizedPeer(context, peers);
            if (!peers.TryGetTrustToken(source.Id, source.Fingerprint, out var trustToken))
                return Results.Unauthorized();
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(trustToken);
            operationCancellation.CancelAfter(TimeSpan.FromSeconds(2));
            var result = await focus.SwitchDisplayForPeerAsync(source, command, operationCancellation.Token);
            return result.Status switch
            {
                "verified" or "issued-unverified" => Results.Ok(result),
                "session-rejected" => Results.Conflict(result),
                "not-ready" => Results.UnprocessableEntity(result),
                _ => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)
            };
        });
        app.MapPost("/peer/v1/display/confirm-arrival", async (HttpContext context, FocusCommand command,
            PeerDirectory peers, FocusCoordinator focus) =>
        {
            var source = RequireAuthorizedPeer(context, peers);
            if (!peers.TryGetTrustToken(source.Id, source.Fingerprint, out var trustToken))
                return Results.Unauthorized();
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(trustToken);
            operationCancellation.CancelAfter(FocusCoordinator.PeerArrivalAttemptWindow);
            PeerDisplayArrivalResponse result;
            try
            {
                result = await focus.ConfirmArrivalForPeerAsync(source, command, operationCancellation.Token);
            }
            catch (OperationCanceledException) when (
                operationCancellation.IsCancellationRequested && !trustToken.IsCancellationRequested)
            {
                result = new PeerDisplayArrivalResponse(false, command.Epoch,
                    "单次画面到达探测超过 2 秒，请稍后重试。");
            }
            return result.Confirmed ? Results.Ok(result) : Results.Conflict(result);
        });
        app.MapPost("/peer/v1/input/release-all", (HttpContext context, FocusCommand command,
            DeviceIdentity identity, PeerDirectory peers, InputCoordinator input) =>
        {
            if (!ValidateFocusCommand(context, command, identity, peers))
                return Results.Conflict(new { released = false, epoch = command.Epoch });
            input.ReleaseIncomingThrough(command.SourceDeviceId, command.Epoch);
            return Results.Ok(new { released = true, epoch = command.Epoch });
        });
        app.MapGet("/peer/v1/input", HandleInputWebSocketAsync);

        app.MapPost("/peer/v1/clipboard", async (HttpContext context, PeerDirectory peers,
            ClipboardCoordinator clipboard, ClipboardWorker worker, CancellationToken cancellationToken) =>
        {
            var source = RequireAuthorizedPeer(context, peers);
            if (!peers.TryGetTrustToken(source.Id, source.Fingerprint, out var trustToken))
                return Results.Unauthorized();
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                trustToken);
            using var applySlot = worker.TryAcquireRemoteApplySlot();
            if (applySlot is null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            ClipboardPacket? packet;
            try
            {
                packet = await context.Request.ReadFromJsonAsync<ClipboardPacket>(
                    JsonOptions,
                    requestCancellation.Token);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { accepted = false, error = "invalid-json" });
            }
            catch (OperationCanceledException) when (trustToken.IsCancellationRequested)
            {
                return Results.Unauthorized();
            }
            if (packet is null)
                return Results.BadRequest(new { accepted = false, error = "missing-packet" });
            var accepted = clipboard.ReceiveRemote(packet, source);
            if (accepted is null) return Results.Ok(new { accepted = false, duplicate = true });
            _ = RequireAuthorizedPeer(context, peers);
            requestCancellation.Token.ThrowIfCancellationRequested();
            try
            {
                await worker.ApplyRemoteAsync(accepted, requestCancellation.Token);
            }
            catch (OperationCanceledException) when (trustToken.IsCancellationRequested)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(new { accepted = true });
        });

        app.MapPost("/peer/v1/files/offers", (HttpContext context, FileOfferPacket packet, PeerDirectory peers,
            FileTransferCoordinator files) =>
        {
            var source = RequireAuthorizedPeer(context, peers);
            return Results.Accepted(value: files.ReceiveOffer(packet, source));
        });
        app.MapPost("/peer/v1/files/offers/{id}/decision", async (string id, HttpContext context, FileDecisionPacket request,
            PeerDirectory peers, FileTransferCoordinator files, CancellationToken cancellationToken) =>
            Results.Ok(await files.HandleRemoteDecisionAsync(id, request, RequireAuthorizedPeer(context, peers), cancellationToken)));
        app.MapPut("/peer/v1/files/offers/{id}/content", async (string id, HttpContext context, HttpRequest request,
            PeerDirectory peers, FileTransferCoordinator files, CancellationToken cancellationToken) =>
            Results.Ok(await files.ReceiveContentAsync(id, request.Body, RequireAuthorizedPeer(context, peers), cancellationToken)))
            .WithMetadata(new RequestSizeLimitAttribute(AgentOptions.MaxFileBytes));
    }

    private static async Task HandleEventWebSocketAsync(
        HttpContext context,
        AppState state,
        LocalAdminService admin)
    {
        var authentication = RequireAdminSession(context);
        if (!context.WebSockets.IsWebSocketRequest ||
            !admin.MatchesCsrf(authentication, context.Request.Query["token"]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var subscription = state.Subscribe();
        await foreach (var item in subscription.Reader.ReadAllAsync(context.RequestAborted))
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, context.RequestAborted);
        }
    }

    private static async Task HandleInputWebSocketAsync(HttpContext context, InputCoordinator input, PeerDirectory peers)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var peer = RequirePeer(context);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[256 * 1024];
        long? acceptedEpoch = null;
        try
        {
            while (!context.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                if (!peers.IsAuthorized(peer.Id, peer.Fingerprint)) break;
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (!peers.IsAuthorized(peer.Id, peer.Fingerprint)) break;
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (!result.EndOfMessage || result.Count > buffer.Length) continue;
                var packet = JsonSerializer.Deserialize<InputBatchPacket>(buffer.AsSpan(0, result.Count), JsonOptions);
                if (packet is not null) acceptedEpoch = packet.Epoch;
                if (packet is not null && !input.AcceptRemoteBatch(peer.Id, packet))
                {
                    input.RevokeIncoming(peer.Id, packet.Epoch);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation,
                        "输入会话或序号无效", context.RequestAborted);
                    break;
                }
            }
        }
        finally
        {
            if (acceptedEpoch is not null) input.RevokeIncoming(peer.Id, acceptedEpoch);
        }
    }

    private static RuntimePeer RequirePeer(HttpContext context) =>
        context.Items.TryGetValue("LanSwitch.Peer", out var value) && value is RuntimePeer peer
            ? peer
            : throw new UnauthorizedAccessException("设备未配对。");

    private static RuntimePeer RequireAuthorizedPeer(HttpContext context, PeerDirectory peers)
    {
        var peer = RequirePeer(context);
        return peers.IsAuthorized(peer.Id, peer.Fingerprint)
            ? peer
            : throw new UnauthorizedAccessException("设备信任已被撤销。");
    }

    private static bool ValidateFocusCommand(HttpContext context, FocusCommand command, DeviceIdentity identity,
        PeerDirectory peers)
    {
        var peer = RequireAuthorizedPeer(context, peers);
        return command.Epoch > 0 &&
            string.Equals(command.SourceDeviceId, peer.Id, StringComparison.Ordinal) &&
            string.Equals(command.TargetDeviceId, identity.DeviceId, StringComparison.Ordinal);
    }

    private static bool IsUnsafe(string method) => method is not "GET" and not "HEAD" and not "OPTIONS";
    internal static bool RequiresAdminAuthorization(PathString path) =>
        path != "/api/v1/session" &&
        path != "/api/v1/auth/status" &&
        path != "/api/v1/auth/setup" &&
        path != "/api/v1/auth/login";

    private static bool IsSessionRevocationEndpoint(PathString path) =>
        path == "/api/v1/auth/logout" ||
        path == "/api/v1/auth/password" ||
        path == "/api/v1/auth/sessions/revoke";

    private static AdminStatusView GetAdminStatus(HttpContext context, LocalAdminService admin)
        => admin.GetStatus(GetAdminAuthentication(context, admin));

    private static AdminAuthenticationResult GetAdminAuthentication(HttpContext context, LocalAdminService admin)
    {
        var authentication = admin.Authenticate(context.Request.Cookies[admin.CookieName]);
        if (authentication.ReplacementToken is not null && authentication.ExpiresAt is { } expiresAt)
            admin.AppendSessionCookie(context.Response, authentication.ReplacementToken, expiresAt);
        return authentication;
    }

    private static AdminAuthenticationResult RequireAdminSession(HttpContext context) =>
        context.Items.TryGetValue(LocalAdminService.ContextItemName, out var value) &&
        value is AdminAuthenticationResult { IsAuthenticated: true } authentication
            ? authentication
            : throw new UnauthorizedAccessException("Administrator login is required.");

    private static IResult AdminLoginResponse(HttpContext context, LocalAdminService admin, AdminLoginResult result)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (result.Succeeded && result.Token is not null && result.ExpiresAt is { } expiresAt)
        {
            admin.AppendSessionCookie(context.Response, result.Token, expiresAt);
            return Results.Ok(admin.GetStatus(result.Token));
        }

        return result.Status switch
        {
            "setup-required" => Results.Json(new { error = result.Error, code = result.Status }, statusCode: 428),
            "recovery-required" => Results.Json(new { error = result.Error, code = "credential-recovery-required" }, statusCode: 423),
            "invalid-bootstrap-token" => Results.Json(new { error = result.Error, code = result.Status }, statusCode: 403),
            "conflict" => Results.Conflict(new { error = result.Error, code = result.Status }),
            "locked" => LockedLoginResult(context, result),
            _ => Results.Json(new
            {
                error = result.Error,
                code = result.Status,
                attemptsRemaining = result.AttemptsRemaining
            }, statusCode: StatusCodes.Status401Unauthorized)
        };
    }

    private static IResult LockedLoginResult(HttpContext context, AdminLoginResult result)
    {
        if (result.LockedUntil is { } lockedUntil)
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling((lockedUntil - DateTimeOffset.UtcNow).TotalSeconds));
            context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return Results.Json(new
        {
            error = result.Error,
            code = result.Status,
            lockedUntil = result.LockedUntil
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }
    internal static ApiExceptionClassification ClassifyException(Exception exception, bool requestAborted) =>
        exception switch
        {
            FocusArrivalTimeoutException => new(StatusCodes.Status504GatewayTimeout, exception.Message),
            OperationCanceledException when requestAborted => new(499, null),
            OperationCanceledException => new(StatusCodes.Status409Conflict,
                LocalizedCancellationDetail(exception)),
            KeyNotFoundException => new(StatusCodes.Status404NotFound, exception.Message),
            FileTransferBusyException or ClipboardDispatcherQueueFullException =>
                new(StatusCodes.Status429TooManyRequests, exception.Message),
            UnauthorizedAccessException => new(StatusCodes.Status401Unauthorized, exception.Message),
            ArgumentException or InvalidDataException or InvalidOperationException =>
                new(StatusCodes.Status400BadRequest, exception.Message),
            _ => new(StatusCodes.Status500InternalServerError,
                "服务器内部错误，请查看本机诊断日志并提供 traceId。")
        };

    private static string LocalizedCancellationDetail(Exception exception) =>
        exception.Message.Any(static character => character > 127)
            ? exception.Message
            : "操作已取消，或已被紧急回切取代。";

    private static bool HasValidLocalOrigin(HttpRequest request)
    {
        var value = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin)) return false;
        var loopback = string.Equals(origin.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(origin.Host, out var address) && IPAddress.IsLoopback(address));
        return loopback &&
            string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
    internal static int? UnsafeApiOriginRejectionStatus(HttpRequest request) =>
        request.Path.StartsWithSegments("/api/v1") &&
        IsUnsafe(request.Method) &&
        !HasExactLocalOrigin(request)
            ? StatusCodes.Status403Forbidden
            : null;

    private static bool HasExactLocalOrigin(HttpRequest request) =>
        !string.IsNullOrWhiteSpace(request.Headers.Origin) && HasValidLocalOrigin(request);
    private static bool IsLoopbackHost(string host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    internal static void ApplyLocalSecurityHeaders(HttpResponse response, PathString path)
    {
        response.Headers.ContentSecurityPolicy =
            "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; " +
            "form-action 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data: blob:; font-src 'self' data:; " +
            "connect-src 'self'";
        response.Headers.XFrameOptions = "DENY";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Permissions-Policy"] = "camera=(), geolocation=(), microphone=()";
        ApplyLocalCachePolicy(response, path);
    }

    internal static void ApplyLocalCachePolicy(HttpResponse response, PathString path)
    {
        if (path.StartsWithSegments("/api"))
        {
            response.Headers.CacheControl = "no-store";
            return;
        }

        // The console previously used stable asset names. Without an explicit policy,
        // browsers could combine a newly upgraded Agent with an older cached UI bundle.
        // Revalidate every local console resource so authentication and API contracts
        // always come from the same installed version.
        response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }
    private static bool IsUnpairedPeerPath(PathString path) =>
        path == "/peer/v1/hello" || path == "/peer/v1/pairings" ||
        (path.StartsWithSegments("/peer/v1/pairings") && path.Value?.EndsWith("/status", StringComparison.Ordinal) == true);

    private static long RequestLimit(PathString path)
    {
        if (path.StartsWithSegments("/api/v1/auth")) return 16 * 1024;
        if (path == "/api/v1/files/offers") return AgentOptions.MaxFileRequestBytes;
        if (path.StartsWithSegments("/peer/v1/files/offers") &&
            path.Value?.EndsWith("/content", StringComparison.Ordinal) == true) return AgentOptions.MaxFileBytes;
        if (path == "/peer/v1/clipboard") return AgentOptions.MaxClipboardRequestBytes;
        if (path == "/peer/v1/pairings") return AgentOptions.MaxPairingRequestBytes;
        return AgentOptions.MaxJsonRequestBytes;
    }

    private static (string Address, int Port) ParseAddress(string value, int defaultPort)
    {
        value = value.Trim();
        if (Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value,
                UriKind.Absolute, out var uri))
            return (uri.Host, uri.IsDefaultPort ? defaultPort : uri.Port);
        throw new ArgumentException("设备地址格式无效。");
    }
}

public sealed record StartPairingRequest(string Address, string Code);
public sealed record ConfirmPairingRequest(bool Approve);
public sealed record SwitchFocusRequest(string? TargetDeviceId);
public sealed record FileDecisionRequest(bool Accept);
public sealed record DisplayFollowRequest(bool Enabled);
public sealed record AdminSetupRequest(string? Username, string Password, string BootstrapToken);
public sealed record AdminLoginRequest(string Username, string Password);
public sealed record AdminPasswordChangeRequest(string CurrentPassword, string NewPassword);
public sealed record AdminSessionRevokeRequest(bool IncludeCurrent = false);
internal sealed record ApiExceptionClassification(int StatusCode, string? Detail);
