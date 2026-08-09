using System.Net.Http.Json;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

public sealed class FocusCoordinator(
    DeviceIdentity identity,
    SettingsStore settings,
    PeerDirectory peers,
    PeerHttpClientFactory clients,
    AppState state,
    InputCoordinator input,
    DisplayCoordinator display)
{
    internal static readonly TimeSpan ForwardArrivalWindow = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan PeerArrivalAttemptWindow = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ArrivalRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly object _transitionGate = new();
    private readonly FocusSwitchCancellation _switchCancellation = new();
    private readonly object _peerDisplayOperationsGate = new();
    private readonly Dictionary<string, PeerDisplaySwitchOperation> _peerDisplayOperations = new(StringComparer.Ordinal);
    internal Func<RuntimePeer, HttpClient>? PeerClientFactoryOverride { get; set; }

    public async Task<FocusView> SwitchAsync(
        string? targetDeviceId,
        CancellationToken cancellationToken,
        bool skipDisplay = false,
        Func<CancellationToken, Task<IDisposable?>>? transitionGuardFactory = null)
    {
        var requestGeneration = _switchCancellation.CaptureGeneration();
        await _switchGate.WaitAsync(cancellationToken);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_switchCancellation.TryRegister(requestGeneration, operation))
        {
            _switchGate.Release();
            throw new OperationCanceledException("切换请求已被紧急释放取消。", cancellationToken);
        }
        var operationToken = operation.Token;
        IDisposable? transitionGuard = null;
        try
        {
            if (transitionGuardFactory is not null)
            {
                transitionGuard = await transitionGuardFactory(operationToken);
                if (transitionGuard is null)
                    throw new OperationCanceledException(
                        "实体信号源跟随条件已变化，本次键鼠切换已取消。",
                        operationToken);
            }
            var current = state.Focus;
            if (string.IsNullOrWhiteSpace(targetDeviceId))
            {
                targetDeviceId = current.IsRemote ? identity.DeviceId : peers.PairedPeers.FirstOrDefault(static peer => peer.Online)?.Id;
            }
            if (string.IsNullOrWhiteSpace(targetDeviceId)) throw new InvalidOperationException("没有在线的已配对设备。");
            if (targetDeviceId == identity.DeviceId)
            {
                state.AddDiagnostic("info", "控制切换", $"请求切回本机；当前阶段={current.Phase}，epoch={current.Epoch}。");
                return EmergencyReleaseNow(
                    current.IsRemote ? "键鼠已切回本机；正在请求当前活动远端回切显示器。" : "切回本机",
                    restoreDisplay: !skipDisplay);
            }
            if (!peers.TryGet(targetDeviceId, out var target) || !target.Paired || !target.Online)
                throw new InvalidOperationException("目标设备未配对或当前离线。");

            state.AddDiagnostic("info", "控制切换",
                $"开始切换到 {target.Name}；跳过显示器={skipDisplay}，请求代次={requestGeneration}。");
            var epoch = Math.Max(current.Epoch + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            state.SetFocus(new FocusView(epoch, current.ActiveDeviceId, current.ActiveDeviceName, "preparing", current.IsRemote));
            using var client = CreatePeerClient(target);
            var command = new FocusCommand(epoch, identity.DeviceId, target.Id);
            var displayMayBeRemote = skipDisplay;
            try
            {
                using var prepared = await client.PostAsJsonAsync("peer/v1/focus/prepare", command, operationToken);
                var prepareResult = await prepared.Content.ReadFromJsonAsync<FocusPrepareResponse>(
                    cancellationToken: operationToken);
                if (!prepared.IsSuccessStatusCode || prepareResult is null || !prepareResult.Ready ||
                    prepareResult.Epoch != epoch || !prepareResult.ReturnDisplayReady)
                    throw new InvalidOperationException(prepareResult?.Message ??
                        "目标设备未确认具备安全回切映射，已取消切换。");
                await input.ReleaseAllAsync(operationToken);
                if (!skipDisplay)
                {
                    var displayResult = await display.SwitchToDeviceAsync(target.Id, operationToken);
                    displayMayBeRemote = displayResult.CommandIssued;
                    state.AddDiagnostic(
                        displayResult.FocusMayProceed ? "info" : "error",
                        "显示器",
                        $"切源结果：已发命令={displayResult.CommandIssued}，已验证={displayResult.Verified}；{displayResult.Message}");
                    if (!displayResult.FocusMayProceed)
                        throw new InvalidOperationException(displayResult.Message);
                }
                var arrival = await RequestPeerArrivalConfirmationAsync(target, command, operationToken);
                if (!arrival.Confirmed)
                    throw new InvalidOperationException(arrival.Message);
                using var committed = await client.PostAsJsonAsync("peer/v1/focus/commit", command, operationToken);
                committed.EnsureSuccessStatusCode();
                var active = new FocusView(epoch, target.Id, target.Name, "remote", true);
                lock (_transitionGate)
                {
                    operationToken.ThrowIfCancellationRequested();
                    if (state.Focus.Epoch > epoch) throw new OperationCanceledException("切换已被紧急释放取代。", operationToken);
                    input.RouteTo(target, epoch);
                    state.SetFocus(active);
                }
                state.AddDiagnostic("success", "控制切换", $"键鼠控制已提交到 {target.Name}，epoch={epoch}。");
                return active;
            }
            catch (Exception exception)
            {
                state.AddDiagnostic("error", "控制切换",
                    $"切换到 {target.Name} 失败：{exception.GetType().Name}：{exception.Message}");
                EmergencyReleaseNow("切换失败，已恢复本机输入。", restoreDisplay: false);
                transitionGuard?.Dispose();
                transitionGuard = null;
                if (displayMayBeRemote)
                    await RollBackDisplayFromPeerSafelyAsync(target, epoch);
                await AbortRemoteSafelyAsync(client, command);
                throw;
            }
        }
        finally
        {
            transitionGuard?.Dispose();
            _switchCancellation.Complete(operation);
            _switchGate.Release();
        }
    }

    public Task<FocusView> EmergencyReleaseAsync(string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EmergencyReleaseNow(reason));
    }

    public FocusView EmergencyReleaseNow(string reason, bool restoreDisplay = true)
        => TryEmergencyReleaseNow(reason, restoreDisplay, static _ => true)!;

    public bool EmergencyReleaseIfOutgoingSessionCurrent(
        long generation,
        long epoch,
        string targetDeviceId,
        string reason)
        => TryEmergencyReleaseNow(
            reason,
            restoreDisplay: true,
            outgoing => outgoing.Generation == generation &&
                outgoing.Epoch == epoch &&
                string.Equals(outgoing.Target?.Id, targetDeviceId, StringComparison.Ordinal)) is not null;

    private FocusView? TryEmergencyReleaseNow(
        string reason,
        bool restoreDisplay,
        Func<OutgoingSessionSnapshot, bool> sessionMatches)
    {
        OutgoingSessionSnapshot outgoing;
        var preserveTransportForDisplayReturn = false;
        FocusView local;
        lock (_transitionGate)
        {
            outgoing = input.GetOutgoingSession();
            if (!sessionMatches(outgoing)) return null;
            _switchCancellation.CancelAll();
            preserveTransportForDisplayReturn = restoreDisplay && outgoing.Target is not null;
            if (preserveTransportForDisplayReturn) outgoing = input.BeginOutgoingDisplayReturn();
            else input.EmergencyReset();
            var epoch = Math.Max(state.Focus.Epoch + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            local = new FocusView(epoch, identity.DeviceId, settings.Snapshot.DeviceName, "local", false);
            state.SetFocus(local);
        }
        state.Publish("notice", new { level = "info", message = reason });
        state.AddDiagnostic("warning", "紧急回切",
            $"{reason}；旧目标={outgoing.Target?.Name ?? "本机"}，旧 epoch={outgoing.Epoch}，恢复显示器={restoreDisplay}。");
        if (outgoing.Target is not null)
        {
            if (preserveTransportForDisplayReturn)
                _ = CompleteDisplayReturnFromActivePeerAsync(outgoing);
            else
                _ = ReleaseRemoteSafelyAsync(outgoing.Target, outgoing.Epoch);
        }
        else if (restoreDisplay)
        {
            _ = ConfirmLocalArrivalSafelyAsync();
        }
        return local;
    }

    public Task<PeerDisplaySwitchResponse> SwitchDisplayForPeerAsync(
        RuntimePeer requestingPeer,
        PeerDisplaySwitchCommand command,
        CancellationToken cancellationToken)
    {
        if (!requestingPeer.Paired || command.Epoch <= 0 ||
            !string.Equals(command.RequestingDeviceId, requestingPeer.Id, StringComparison.Ordinal) ||
            !string.Equals(command.ActiveDeviceId, identity.DeviceId, StringComparison.Ordinal) ||
            !string.Equals(command.TargetDeviceId, requestingPeer.Id, StringComparison.Ordinal))
        {
            return Task.FromResult(PeerDisplaySwitchResponse.SessionRejected(
                command.Epoch, "切屏请求的设备身份或目标不匹配。"));
        }

        PeerDisplaySwitchOperation operation;
        lock (_peerDisplayOperationsGate)
        {
            if (_peerDisplayOperations.TryGetValue(requestingPeer.Id, out var existing))
            {
                if (existing.Epoch == command.Epoch) return existing.Task;
                if (!existing.Task.IsCompleted || existing.Epoch > command.Epoch)
                {
                    return Task.FromResult(PeerDisplaySwitchResponse.SessionRejected(
                        command.Epoch, "已有更新或尚未完成的显示器回切事务。"));
                }
            }

            var completion = new TaskCompletionSource<PeerDisplaySwitchResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            operation = new PeerDisplaySwitchOperation(command.Epoch, completion.Task, completion);
            _peerDisplayOperations[requestingPeer.Id] = operation;
        }

        _ = RunPeerDisplaySwitchAsync(requestingPeer, command, operation, cancellationToken);
        return operation.Task;
    }

    private async Task RunPeerDisplaySwitchAsync(
        RuntimePeer requestingPeer,
        PeerDisplaySwitchCommand command,
        PeerDisplaySwitchOperation operation,
        CancellationToken cancellationToken)
    {
        PeerDisplaySwitchResponse response;
        try
        {
            if (!input.TryBeginIncomingDisplaySwitch(command.Epoch, requestingPeer.Id))
            {
                response = PeerDisplaySwitchResponse.SessionRejected(
                    command.Epoch, "切屏请求与当前 prepared/committed 会话不匹配，或该 epoch 已使用。");
                operation.Completion.TrySetResult(response);
                return;
            }

            var result = await display.SwitchToDeviceForPeerReturnAsync(command.TargetDeviceId, cancellationToken);
            response = result.CommandIssued
                ? new PeerDisplaySwitchResponse(true, command.Epoch, result.Verified,
                    result.Verified ? "verified" : "issued-unverified", result.Message)
                : new PeerDisplaySwitchResponse(false, command.Epoch, false,
                    "not-ready", $"未执行安全切屏：{result.Message}");
        }
        catch (Exception exception)
        {
            response = new PeerDisplaySwitchResponse(false, command.Epoch, false,
                "failed", $"显示器回切失败：{exception.Message}");
        }
        finally
        {
            input.RevokeIncoming(requestingPeer.Id, command.Epoch);
            input.CompleteIncomingDisplaySwitch(command.Epoch, requestingPeer.Id);
        }
        operation.Completion.TrySetResult(response);
    }

    public async Task<PeerDisplayArrivalResponse> ConfirmArrivalForPeerAsync(
        RuntimePeer requestingPeer,
        FocusCommand command,
        CancellationToken cancellationToken)
    {
        if (!requestingPeer.Paired || command.Epoch <= 0 ||
            !string.Equals(command.SourceDeviceId, requestingPeer.Id, StringComparison.Ordinal) ||
            !string.Equals(command.TargetDeviceId, identity.DeviceId, StringComparison.Ordinal) ||
            !input.IsIncomingPrepared(command.Epoch, requestingPeer.Id))
        {
            return new PeerDisplayArrivalResponse(false, command.Epoch,
                "画面到达确认与当前 prepared 焦点会话不匹配。");
        }

        var confirmation = await display.ConfirmLocalArrivalAsync(cancellationToken);
        return new PeerDisplayArrivalResponse(confirmation.Confirmed, command.Epoch, confirmation.Message);
    }

    private async Task ReleaseRemoteSafelyAsync(RuntimePeer remote, long epoch)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            using var client = CreatePeerClient(remote);
            client.Timeout = TimeSpan.FromMilliseconds(750);
            using var response = await client.PostAsJsonAsync("peer/v1/input/release-all",
                new FocusCommand(epoch, identity.DeviceId, remote.Id), timeout.Token);
        }
        catch { }
    }

    private async Task CompleteDisplayReturnFromActivePeerAsync(OutgoingSessionSnapshot outgoing)
    {
        var remote = outgoing.Target!;
        try
        {
            using var switchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await RequestDisplaySwitchFromActivePeerAsync(remote, identity.DeviceId, outgoing.Epoch, switchTimeout.Token);
            using var arrivalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var arrival = await ConfirmLocalArrivalWithRetryAsync(arrivalTimeout.Token);
            if (!arrival.Confirmed) throw new InvalidOperationException(arrival.Message);
            state.Publish("notice", new { level = "info", message = "活动远端已回切显示器，并经本机 fresh 探测确认。" });
        }
        catch (Exception exception)
        {
            state.Publish("notice", new
            {
                level = "warning",
                message = $"键鼠已在本机，但自动回切显示器未确认：{exception.Message} 请使用显示器实体键。"
            });
        }
        finally
        {
            input.CompleteOutgoingDisplayReturn(outgoing.Generation);
            await ReleaseRemoteSafelyAsync(remote, outgoing.Epoch);
        }
    }

    private async Task RollBackDisplayFromPeerSafelyAsync(RuntimePeer peer, long epoch)
    {
        try
        {
            using var switchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await RequestDisplaySwitchFromActivePeerAsync(peer, identity.DeviceId, epoch, switchTimeout.Token);
            using var arrivalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var arrival = await ConfirmLocalArrivalWithRetryAsync(arrivalTimeout.Token);
            if (!arrival.Confirmed) throw new InvalidOperationException(arrival.Message);
        }
        catch (Exception exception)
        {
            state.Publish("notice", new
            {
                level = "warning",
                message = $"键鼠已恢复本机，但显示器回滚未确认：{exception.Message} 请使用显示器实体键。"
            });
        }
        finally
        {
            await ReleaseRemoteSafelyAsync(peer, epoch);
        }
    }

    private async Task<PeerDisplaySwitchResponse> RequestDisplaySwitchFromActivePeerAsync(
        RuntimePeer activePeer,
        string targetDeviceId,
        long epoch,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = CreatePeerClient(activePeer);
        var command = new PeerDisplaySwitchCommand(epoch, identity.DeviceId, activePeer.Id, targetDeviceId);
        using var response = await client.PostAsJsonAsync("peer/v1/display/switch", command, timeout.Token);
        var result = await response.Content.ReadFromJsonAsync<PeerDisplaySwitchResponse>(cancellationToken: timeout.Token);
        if (!response.IsSuccessStatusCode || result is null || !result.CommandIssued || result.Epoch != epoch)
            throw new InvalidOperationException(result?.Message ?? "活动远端未确认显示器切换。");
        return result;
    }

    private async Task<PeerDisplayArrivalResponse> RequestPeerArrivalConfirmationAsync(
        RuntimePeer target,
        FocusCommand command,
        CancellationToken cancellationToken)
    {
        return await RunWithArrivalDeadlineAsync(
            token => RetryArrivalConfirmationAsync(
                attemptToken => RequestPeerArrivalConfirmationOnceAsync(target, command, attemptToken),
                static result => result.Confirmed,
                () => peers.IsAuthorized(target.Id, target.Fingerprint),
                token,
                maximumAttempts: int.MaxValue,
                retryDelay: ArrivalRetryDelay),
            cancellationToken,
            ForwardArrivalWindow);
    }

    private async Task<PeerDisplayArrivalResponse> RequestPeerArrivalConfirmationOnceAsync(
        RuntimePeer target,
        FocusCommand command,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PeerArrivalAttemptWindow);
        try
        {
            using var client = CreatePeerClient(target);
            using var response = await client.PostAsJsonAsync(
                "peer/v1/display/confirm-arrival", command, timeout.Token);
            var result = await response.Content.ReadFromJsonAsync<PeerDisplayArrivalResponse>(
                cancellationToken: timeout.Token);
            if (result is null || result.Epoch != command.Epoch)
                throw new InvalidOperationException(result?.Message ?? "目标设备未确认画面到达。");
            return result;
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new PeerDisplayArrivalResponse(false, command.Epoch,
                "单次画面到达探测超过 2 秒，正在重试。");
        }
    }

    private async Task ConfirmLocalArrivalSafelyAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await ConfirmLocalArrivalWithRetryAsync(timeout.Token);
            if (!result.Confirmed)
                state.Publish("notice", new { level = "warning", message = result.Message });
        }
        catch { }
    }

    private Task<DisplayArrivalConfirmation> ConfirmLocalArrivalWithRetryAsync(
        CancellationToken cancellationToken) =>
        RetryArrivalConfirmationAsync(
            display.ConfirmLocalArrivalAsync,
            static result => result.Confirmed,
            static () => true,
            cancellationToken);

    internal static async Task<T> RetryArrivalConfirmationAsync<T>(
        Func<CancellationToken, Task<T>> attempt,
        Func<T, bool> isConfirmed,
        Func<bool> sessionIsValid,
        CancellationToken cancellationToken,
        int maximumAttempts = 4,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(isConfirmed);
        ArgumentNullException.ThrowIfNull(sessionIsValid);
        if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

        var delay = retryDelay ?? TimeSpan.FromMilliseconds(250);
        T? last = default;
        for (var attemptNumber = 1; attemptNumber <= maximumAttempts; attemptNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sessionIsValid())
                throw new OperationCanceledException(
                    "配对焦点会话已失效，画面到达确认已取消。", cancellationToken);

            last = await attempt(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!sessionIsValid())
                throw new OperationCanceledException(
                    "配对焦点会话在画面到达确认期间结束。", cancellationToken);
            if (isConfirmed(last)) return last;
            if (attemptNumber < maximumAttempts)
                await Task.Delay(delay, cancellationToken);
        }

        return last!;
    }

    internal static async Task<T> RunWithArrivalDeadlineAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken externalCancellation,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var window = timeout ?? ForwardArrivalWindow;
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        deadline.CancelAfter(window);
        try
        {
            return await operation(deadline.Token);
        }
        catch (OperationCanceledException exception) when (
            deadline.IsCancellationRequested && !externalCancellation.IsCancellationRequested)
        {
            throw new FocusArrivalTimeoutException(
                $"等待目标设备确认画面到达已超时（约 {window.TotalSeconds:0.#} 秒），键鼠已恢复本机并尝试回滚显示器。",
                exception);
        }
    }

    private HttpClient CreatePeerClient(RuntimePeer peer) =>
        PeerClientFactoryOverride?.Invoke(peer) ?? clients.Create(peer);

    private static async Task AbortRemoteSafelyAsync(HttpClient client, FocusCommand command)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            using var response = await client.PostAsJsonAsync("peer/v1/focus/abort", command, timeout.Token);
        }
        catch { }
    }

    public FocusPrepareResponse PrepareRemote(FocusCommand command)
    {
        if (!input.PrepareIncoming(command.Epoch, command.SourceDeviceId))
            return new FocusPrepareResponse(false, command.Epoch, false,
                "焦点 epoch 已过期、冲突，或显示器回切事务尚未完成。");
        var readiness = display.GetPeerReturnReadiness(command.SourceDeviceId);
        if (!readiness.Ready)
        {
            input.RejectIncomingPreparation(command.Epoch, command.SourceDeviceId);
            return new FocusPrepareResponse(false, command.Epoch, false, readiness.Message);
        }
        return new FocusPrepareResponse(true, command.Epoch, true, readiness.Message);
    }

    public bool CommitRemote(FocusCommand command) => input.CommitIncoming(command.Epoch, command.SourceDeviceId);
    public void AbortRemote(FocusCommand command) => input.AbortIncoming(command.Epoch, command.SourceDeviceId);
}

public sealed record FocusCommand(long Epoch, string SourceDeviceId, string TargetDeviceId);
public sealed record PeerDisplaySwitchCommand(
    long Epoch,
    string RequestingDeviceId,
    string ActiveDeviceId,
    string TargetDeviceId);
public sealed record FocusPrepareResponse(bool Ready, long Epoch, bool ReturnDisplayReady, string Message);
public sealed record PeerDisplayArrivalResponse(bool Confirmed, long Epoch, string Message);
public sealed class FocusArrivalTimeoutException(string message, Exception? innerException = null)
    : TimeoutException(message, innerException);
public sealed record PeerDisplaySwitchResponse(
    bool CommandIssued,
    long Epoch,
    bool Verified,
    string Status,
    string Message)
{
    public static PeerDisplaySwitchResponse SessionRejected(long epoch, string message) =>
        new(false, epoch, false, "session-rejected", message);
}

internal sealed record PeerDisplaySwitchOperation(
    long Epoch,
    Task<PeerDisplaySwitchResponse> Task,
    TaskCompletionSource<PeerDisplaySwitchResponse> Completion);

internal sealed class FocusSwitchCancellation
{
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private long _generation;

    internal long CaptureGeneration()
    {
        lock (_gate) return _generation;
    }

    internal bool TryRegister(long expectedGeneration, CancellationTokenSource operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            if (expectedGeneration != _generation) return false;
            if (_active is not null)
                throw new InvalidOperationException("A focus switch is already registered.");
            _active = operation;
            return true;
        }
    }

    internal void Complete(CancellationTokenSource operation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, operation)) _active = null;
        }
    }

    internal void CancelAll()
    {
        CancellationTokenSource? active;
        lock (_gate)
        {
            _generation++;
            active = _active;
        }

        try { active?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
