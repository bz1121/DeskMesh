using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

/// <summary>
/// Coordinates physical-input following across paired agents. The agent on the
/// keyboard host is the only subscriber and therefore the only agent allowed to
/// change the keyboard route. A peer merely publishes a fresh, stable observation
/// that its own input is active.
/// </summary>
public sealed class DistributedPhysicalFollowService(
    DeviceIdentity identity,
    SettingsStore settings,
    PeerDirectory peers,
    PeerHttpClientFactory clients,
    DisplayCoordinator display,
    FocusCoordinator focus,
    AppState state,
    ILogger<DistributedPhysicalFollowService> logger) : BackgroundService
{
    private static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SubscriptionRenewal = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PublishThrottle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConflictBackoff = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan FocusTransitionQuietPeriod = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private readonly Dictionary<string, OutboundFollowSession> _outbound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InboundFollowSession> _inbound = new(StringComparer.Ordinal);
    private readonly StableDisplayObservationTracker _localTracker = new();
    private readonly StableDisplayObservationTracker _publisherTracker = new();
    private readonly SemaphoreSlim _observationSwitchGate = new(1, 1);
    private readonly FocusTransitionObservationGate _focusTransitionGate = new(FocusTransitionQuietPeriod);

    internal int InboundSubscriptionCount
    {
        get { lock (_gate) return _inbound.Count; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "分布式实体切源跟随轮询失败。");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var config = settings.Snapshot;
        var subscriberEnabled = config.PhysicalFollowEnabled && !config.DdcWriteOnlyEnabled;
        if (subscriberEnabled)
        {
            ClearInboundSubscriptions();
            await RenewOutboundSubscriptionsAsync(config, cancellationToken);
        }
        else
        {
            await ClearOutboundSubscriptionsAsync(cancellationToken);
        }

        PruneExpiredInboundSessions();
        var focusSnapshot = state.Focus;
        var focusTimestamp = Stopwatch.GetTimestamp();
        var suppressFollow = _focusTransitionGate.ObserveFocus(
            focusSnapshot.ActiveDeviceId, focusTimestamp, out var focusChanged);
        if (focusChanged)
        {
            _localTracker.Reset();
            _publisherTracker.Reset();
            state.AddDiagnostic("info", "实体跟随",
                $"控制目标已切换为 {focusSnapshot.ActiveDeviceName}；旧 DDC 样本已清空，跟随静默 {FocusTransitionQuietPeriod.TotalSeconds:0} 秒。");
        }
        if (suppressFollow) return;
        if (!subscriberEnabled && !HasInboundSubscribers())
        {
            _localTracker.Reset();
            _publisherTracker.Reset();
            return;
        }

        var observation = await ReadFreshMappedDeviceAsync(config, cancellationToken);
        var observedDeviceId = observation?.DeviceId;
        if (subscriberEnabled)
        {
            if (!_focusTransitionGate.AcceptObservation(
                    observedDeviceId, Stopwatch.GetTimestamp(), out var transitionArmed))
            {
                _localTracker.Reset();
                if (transitionArmed)
                {
                    state.AddDiagnostic("info", "实体跟随",
                        "已确认显示器离开旧输入；后续新的稳定输入变化可以触发实体跟随。");
                }
                return;
            }
            var observationKey = observation is null
                ? null
                : $"{observation.DeviceId}\0{observation.MonitorId}\0{observation.Input}";
            if (!_localTracker.Observe(observationKey, out _))
            {
                if (observedDeviceId is null)
                {
                    state.SetDisplay(state.Display with
                    {
                        PhysicalFollowEnabled = true,
                        Message = "当前输入端已无法读取 DDC/CI；实体跟随仍在等待远端 Agent 的稳定确认。"
                    });
                }
            }
            else
            {
                await FollowStableLocalObservationAsync(observation!, cancellationToken);
            }
        }

        var publisherObservationKey = observation is not null && observation.DeviceId == identity.DeviceId
            ? $"{observation.DeviceId}\0{observation.MonitorId}\0{observation.Input}"
            : null;
        if (HasInboundSubscribers() &&
            _publisherTracker.Observe(publisherObservationKey, out _))
        {
            await PublishSelfActiveAsync(observation!, cancellationToken);
        }
    }

    private async Task<PhysicalFollowMappedObservation?> ReadFreshMappedDeviceAsync(
        AgentSettings config,
        CancellationToken cancellationToken)
    {
        if (config.DdcWriteOnlyEnabled) return null;
        var fresh = await display.ProbeAsync(cancellationToken);
        config = settings.Snapshot;
        var matches = (from probe in fresh
                       where probe.Supported && probe.Stable && probe.CurrentInput is not null
                       from mapping in config.DisplayMappings
                       where string.Equals(mapping.MonitorId, probe.MonitorId, StringComparison.OrdinalIgnoreCase) &&
                              mapping.VcpValue == probe.CurrentInput.GetValueOrDefault()
                       select new PhysicalFollowMappedObservation(
                           mapping.DeviceId, mapping.MonitorId, mapping.VcpValue))
            .Distinct()
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private async Task FollowStableLocalObservationAsync(
        PhysicalFollowMappedObservation observation,
        CancellationToken cancellationToken)
    {
        var targetDeviceId = observation.DeviceId;
        var current = state.Focus;
        if (current.Phase == "preparing" || string.Equals(current.ActiveDeviceId, targetDeviceId, StringComparison.Ordinal))
            return;

        if (!string.Equals(targetDeviceId, identity.DeviceId, StringComparison.Ordinal) &&
            (!peers.TryGet(targetDeviceId, out var peer) || !peer.Paired || !peer.Online))
            return;

        try
        {
            state.AddDiagnostic("info", "实体跟随",
                $"连续两次读取到映射 {targetDeviceId}，准备跟随切换键鼠。");
            await focus.SwitchAsync(targetDeviceId, cancellationToken, skipDisplay: true,
                transitionGuardFactory: token => display.TryAcquirePhysicalFollowLeaseAsync(
                    targetDeviceId, observation.MonitorId, observation.Input, token));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state.AddDiagnostic("error", "实体跟随", $"跟随切换失败：{exception.Message}");
            logger.LogDebug(exception, "根据本机稳定输入读数切换键鼠失败。");
        }
    }

    private async Task RenewOutboundSubscriptionsAsync(AgentSettings config, CancellationToken cancellationToken)
    {
        await _observationSwitchGate.WaitAsync(cancellationToken);
        try { await RenewOutboundSubscriptionsCoreAsync(config, cancellationToken); }
        finally { _observationSwitchGate.Release(); }
    }

    private async Task RenewOutboundSubscriptionsCoreAsync(AgentSettings config, CancellationToken cancellationToken)
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var peer in peers.PairedPeers.Where(peer => peer.Online &&
                     config.DisplayMappings.Any(mapping => mapping.DeviceId == peer.Id)))
        {
            OutboundFollowSession session;
            lock (_gate)
            {
                if (!_outbound.TryGetValue(peer.Id, out session!) || session.TrustToken.IsCancellationRequested)
                {
                    if (!peers.TryGetTrustToken(peer.Id, peer.Fingerprint, out var trustToken)) continue;
                    session = CreateOutboundSession(peer, trustToken);
                    _outbound[peer.Id] = session;
                }
                if (session.LastRenewedTimestamp != 0 &&
                    Stopwatch.GetElapsedTime(session.LastRenewedTimestamp, now) >= SubscriptionLifetime)
                {
                    session = CreateOutboundSession(peer, session.TrustToken);
                    _outbound[peer.Id] = session;
                }
                var retryInterval = session.ConflictBackoff ? ConflictBackoff : SubscriptionRenewal;
                if (session.LastAttemptTimestamp != 0 &&
                    Stopwatch.GetElapsedTime(session.LastAttemptTimestamp, now) < retryInterval) continue;
                session = session with { LastAttemptTimestamp = now };
                _outbound[peer.Id] = session;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.TrustToken);
                linked.CancelAfter(TimeSpan.FromSeconds(2));
                using var client = clients.Create(peer);
                var request = new PhysicalFollowSubscriptionCommand(
                    session.SessionId, identity.DeviceId, peer.Id, (int)SubscriptionLifetime.TotalMilliseconds);
                using var response = await client.PostAsJsonAsync("peer/v1/physical-follow/subscriptions", request, linked.Token);
                var result = await response.Content.ReadFromJsonAsync<PhysicalFollowSubscriptionResponse>(
                    cancellationToken: linked.Token);
                if (!response.IsSuccessStatusCode || result is null || !result.Accepted)
                {
                    lock (_gate)
                    {
                        if (_outbound.TryGetValue(peer.Id, out var current) &&
                            current.SessionId == session.SessionId)
                            _outbound[peer.Id] = current with { ConflictBackoff = true };
                    }
                    state.Publish("notice", new
                    {
                        level = "warning",
                        message = "实体输入跟随订阅被拒绝；请只在连接键盘和鼠标的电脑上开启。已进入退避重试。"
                    });
                    continue;
                }
                lock (_gate)
                {
                    if (_outbound.TryGetValue(peer.Id, out var current) && current.SessionId == session.SessionId)
                    {
                        if (result.Created && current.LastRenewedTimestamp != 0)
                        {
                            _outbound[peer.Id] = CreateOutboundSession(peer, current.TrustToken);
                        }
                        else
                        {
                            _outbound[peer.Id] = current with
                            {
                                LastRenewedTimestamp = Stopwatch.GetTimestamp(),
                                ConflictBackoff = false
                            };
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(exception, "续订实体跟随远端观察会话失败：{PeerId}", peer.Id);
            }
        }
    }

    private async Task ClearOutboundSubscriptionsAsync(CancellationToken cancellationToken)
    {
        await _observationSwitchGate.WaitAsync(cancellationToken);
        try { await ClearOutboundSubscriptionsCoreAsync(cancellationToken); }
        finally { _observationSwitchGate.Release(); }
    }

    private async Task ClearOutboundSubscriptionsCoreAsync(CancellationToken cancellationToken)
    {
        OutboundFollowSession[] sessions;
        lock (_gate)
        {
            if (_outbound.Count == 0) return;
            sessions = [.. _outbound.Values];
            _outbound.Clear();
        }
        _localTracker.Reset();
        foreach (var session in sessions)
        {
            if (!peers.TryGet(session.PeerId, out var peer) || !peer.Paired) continue;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.TrustToken);
                linked.CancelAfter(TimeSpan.FromSeconds(1));
                using var client = clients.Create(peer);
                using var response = await client.SendAsync(new HttpRequestMessage(
                    HttpMethod.Delete, $"peer/v1/physical-follow/subscriptions/{session.SessionId}"), linked.Token);
            }
            catch { }
        }
    }

    public PhysicalFollowSubscriptionResponse AcceptSubscription(
        RuntimePeer subscriber,
        PhysicalFollowSubscriptionCommand command,
        CancellationToken trustToken)
    {
        var now = Stopwatch.GetTimestamp();
        var config = settings.Snapshot;
        if (config.PhysicalFollowEnabled || config.DdcWriteOnlyEnabled)
            return new(false, "本机正在作为实体跟随订阅者，或处于只写模式，不能同时充当远端发布者。");
        if (!PhysicalFollowProtocol.IsValidSessionId(command.SessionId) ||
            !string.Equals(command.SubscriberDeviceId, subscriber.Id, StringComparison.Ordinal) ||
            !string.Equals(command.PublisherDeviceId, identity.DeviceId, StringComparison.Ordinal) ||
            command.LeaseMilliseconds is < 2_000 or > 10_000)
            return new(false, "实体跟随订阅的设备身份、会话或有效期无效。");
        var readiness = display.GetPeerReturnReadiness(subscriber.Id);
        if (!readiness.Ready) RemoveAnyInboundSubscription(subscriber.Id);
        if (!readiness.Ready)
            return new(false, $"发布端显示器映射尚未满足安全条件：{readiness.Message}");

        CancellationTokenRegistration? registrationToDispose = null;
        var created = false;
        lock (_gate)
        {
            var hasExisting = _inbound.TryGetValue(subscriber.Id, out var existing);
            var refresh = hasExisting && existing!.SessionId == command.SessionId &&
                !IsInboundExpired(existing, now);
            if (refresh)
            {
                _inbound[subscriber.Id] = RefreshInboundSession(
                    existing!, now, TimeSpan.FromMilliseconds(command.LeaseMilliseconds));
            }
            else
            {
                if (hasExisting) registrationToDispose = existing!.TrustRegistration;
                var registration = trustToken.Register(() => RemoveInboundSubscriptionAfterTrustRevocation(
                    subscriber.Id, command.SessionId));
                _inbound[subscriber.Id] = new InboundFollowSession(
                    command.SessionId, subscriber.Id, subscriber.Fingerprint,
                    now, TimeSpan.FromMilliseconds(command.LeaseMilliseconds), 0, 0,
                    trustToken, registration);
                created = true;
            }
        }
        registrationToDispose?.Dispose();
        if (created && !trustToken.IsCancellationRequested)
            return new PhysicalFollowSubscriptionResponse(true, true,
                "已创建新的实体输入跟随发布会话。");
        if (trustToken.IsCancellationRequested)
        {
            RemoveInboundSubscription(subscriber.Id, command.SessionId);
            return new(false, "实体跟随订阅对应的设备信任已经撤销。");
        }
        return new(true, "已建立临时实体跟随观察会话。");
    }

    public bool RemoveInboundSubscription(string peerId, string sessionId)
    {
        CancellationTokenRegistration registration;
        lock (_gate)
        {
            if (!_inbound.TryGetValue(peerId, out var existing) || existing.SessionId != sessionId) return false;
            _inbound.Remove(peerId);
            registration = existing.TrustRegistration;
        }
        registration.Dispose();
        return true;
    }

    private void RemoveAnyInboundSubscription(string peerId)
    {
        CancellationTokenRegistration? registration = null;
        lock (_gate)
        {
            if (_inbound.Remove(peerId, out var existing))
                registration = existing.TrustRegistration;
        }
        registration?.Dispose();
    }

    private void RemoveInboundSubscriptionAfterTrustRevocation(string peerId, string sessionId)
    {
        // A CancellationTokenRegistration must not be synchronously disposed from
        // inside its own callback because Dispose may wait for the callback.
        lock (_gate)
        {
            if (_inbound.TryGetValue(peerId, out var existing) && existing.SessionId == sessionId)
                _inbound.Remove(peerId);
        }
    }

    public async Task<PhysicalFollowObservationResponse> AcceptObservationAsync(
        RuntimePeer publisher,
        PhysicalFollowObservation notification,
        CancellationToken cancellationToken)
    {
        OutboundFollowSession session;
        lock (_gate)
        {
            if (!_outbound.TryGetValue(publisher.Id, out session!) ||
                session.LastRenewedTimestamp == 0 ||
                Stopwatch.GetElapsedTime(session.LastRenewedTimestamp, Stopwatch.GetTimestamp()) >
                    SubscriptionLifetime + TimeSpan.FromSeconds(2) ||
                !PhysicalFollowProtocol.IsObservationAccepted(
                    session.SessionId, session.LastSequence, publisher.Id, notification))
                return new(false, "实体跟随确认已过期、重复，或不属于当前信任会话。");
        }
        var config = settings.Snapshot;
        var observationPermit = await display.TryCreatePhysicalFollowObservationPermitAsync(
            publisher.Id, cancellationToken);
        if (observationPermit is null)
            return new(false, "Physical-follow settings changed before the observation could be accepted.");
        if (!config.PhysicalFollowEnabled || config.DdcWriteOnlyEnabled ||
            !config.DisplayMappings.Any(mapping => mapping.DeviceId == publisher.Id))
            return new(false, "本机未启用实体跟随，或缺少该发布设备的映射。");

        lock (_gate)
        {
            if (!_outbound.TryGetValue(publisher.Id, out var current) || current.SessionId != session.SessionId ||
                notification.Sequence <= current.LastSequence)
                return new(false, "实体跟随确认已经被更新的会话取代。");
            _outbound[publisher.Id] = current with { LastSequence = notification.Sequence };
        }

        await _observationSwitchGate.WaitAsync(cancellationToken);
        try
        {
            var latestConfig = settings.Snapshot;
            if (!latestConfig.PhysicalFollowEnabled || latestConfig.DdcWriteOnlyEnabled ||
                !latestConfig.DisplayMappings.Any(mapping => mapping.DeviceId == publisher.Id))
                return new(false, "实体跟随已关闭或映射已变化，未执行键鼠切换。");
            if (state.Focus.Phase != "preparing" &&
                !string.Equals(state.Focus.ActiveDeviceId, publisher.Id, StringComparison.Ordinal))
            {
                try
                {
                    await focus.SwitchAsync(publisher.Id, cancellationToken, skipDisplay: true,
                        transitionGuardFactory: token =>
                            TryAcquireRemoteObservationTransitionLeaseAsync(
                                publisher, notification, observationPermit, token));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogDebug(exception, "根据远端稳定输入确认切换键鼠失败：{PeerId}", publisher.Id);
                    return new(false, $"远端输入已确认，但键鼠切换失败：{exception.Message}");
                }
            }
            return new(true, "已接受远端稳定输入确认。");
        }
        finally
        {
            _observationSwitchGate.Release();
        }
    }

    private async Task<IDisposable?> TryAcquireRemoteObservationTransitionLeaseAsync(
        RuntimePeer publisher,
        PhysicalFollowObservation notification,
        DisplayFollowObservationPermit observationPermit,
        CancellationToken cancellationToken)
    {
        if (observationPermit.ConfigurationToken.IsCancellationRequested) return null;
        var displayLease = await display.TryAcquirePhysicalFollowLeaseAsync(
            publisher.Id, cancellationToken);
        if (displayLease is null) return null;

        lock (_gate)
        {
            if (!observationPermit.ConfigurationToken.IsCancellationRequested &&
                _outbound.TryGetValue(publisher.Id, out var current) &&
                IsObservationTransitionCurrent(current, notification) &&
                current.LastRenewedTimestamp != 0 &&
                Stopwatch.GetElapsedTime(current.LastRenewedTimestamp, Stopwatch.GetTimestamp()) <=
                    SubscriptionLifetime + TimeSpan.FromSeconds(2))
                return displayLease;
        }

        displayLease.Dispose();
        return null;
    }

    private async Task PublishSelfActiveAsync(
        PhysicalFollowMappedObservation observation,
        CancellationToken cancellationToken)
    {
        InboundFollowSession[] sessions;
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            sessions = [.. _inbound.Values.Where(session =>
                !IsInboundExpired(session, now) &&
                (session.LastPublishedTimestamp == 0 ||
                 Stopwatch.GetElapsedTime(session.LastPublishedTimestamp, now) >= PublishThrottle))];
        }
        foreach (var session in sessions)
        {
            if (!peers.TryGet(session.PeerId, out var peer) || !peer.Paired ||
                !peers.IsAuthorized(peer.Id, peer.Fingerprint) ||
                !string.Equals(peer.Fingerprint, session.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                RemoveInboundSubscription(session.PeerId, session.SessionId);
                continue;
            }
            var publishPermit = await display.TryAcquirePhysicalFollowPublisherLeaseAsync(
                session.PeerId, observation.MonitorId, observation.Input, cancellationToken);
            if (publishPermit is null)
            {
                RemoveInboundSubscription(session.PeerId, session.SessionId);
                continue;
            }
            var readiness = display.GetPeerReturnReadiness(session.PeerId);
            if (!readiness.Ready)
            {
                RemoveInboundSubscription(session.PeerId, session.SessionId);
                continue;
            }
            try
            {
                long nextSequence;
                lock (_gate)
                {
                    if (!_inbound.TryGetValue(session.PeerId, out var current) ||
                        current.SessionId != session.SessionId ||
                        current.TrustToken.IsCancellationRequested ||
                        IsInboundExpired(current, Stopwatch.GetTimestamp()))
                        continue;
                    var advanced = AdvanceInboundPublish(current, Stopwatch.GetTimestamp());
                    nextSequence = advanced.LastSequence;
                    _inbound[session.PeerId] = advanced;
                }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, session.TrustToken, publishPermit.ConfigurationToken);
                linked.CancelAfter(TimeSpan.FromSeconds(2));
                linked.Token.ThrowIfCancellationRequested();
                if (!peers.IsAuthorized(peer.Id, peer.Fingerprint)) continue;
                using var client = clients.Create(peer);
                using var response = await client.PostAsJsonAsync("peer/v1/physical-follow/observations",
                    new PhysicalFollowObservation(session.SessionId, nextSequence, identity.DeviceId), linked.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(exception, "发布远端输入稳定确认失败：{PeerId}", session.PeerId);
            }
        }
    }

    private bool HasInboundSubscribers()
    {
        lock (_gate) return _inbound.Count > 0;
    }

    private void PruneExpiredInboundSessions()
    {
        var now = Stopwatch.GetTimestamp();
        InboundFollowSession[] removed;
        lock (_gate)
        {
            removed = [.. _inbound.Values.Where(value => IsInboundExpired(value, now) ||
                value.TrustToken.IsCancellationRequested)];
            foreach (var session in removed)
            {
                _inbound.Remove(session.PeerId);
            }
        }
        foreach (var session in removed) session.TrustRegistration.Dispose();
    }

    private void ClearInboundSubscriptions()
    {
        InboundFollowSession[] removed;
        lock (_gate)
        {
            removed = [.. _inbound.Values];
            _inbound.Clear();
        }
        foreach (var session in removed) session.TrustRegistration.Dispose();
        _publisherTracker.Reset();
    }

    private static OutboundFollowSession CreateOutboundSession(
        RuntimePeer peer,
        CancellationToken trustToken) =>
        new(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            peer.Id, peer.Fingerprint, trustToken, 0, 0, false, 0);

    private static bool IsInboundExpired(InboundFollowSession session, long now) =>
        Stopwatch.GetElapsedTime(session.LastRenewedTimestamp, now) >= session.Lease;

    internal static InboundFollowSession RefreshInboundSession(
        InboundFollowSession session,
        long renewedTimestamp,
        TimeSpan lease) =>
        session with { LastRenewedTimestamp = renewedTimestamp, Lease = lease };

    internal static bool IsObservationTransitionCurrent(
        OutboundFollowSession session,
        PhysicalFollowObservation notification) =>
        session.SessionId == notification.SessionId &&
        session.LastSequence == notification.Sequence &&
        !session.TrustToken.IsCancellationRequested;

    internal static InboundFollowSession AdvanceInboundPublish(
        InboundFollowSession session,
        long publishedTimestamp) =>
        session with
        {
            LastSequence = checked(session.LastSequence + 1),
            LastPublishedTimestamp = publishedTimestamp
        };

    internal InboundFollowSession? GetInboundSessionForTests(string peerId)
    {
        lock (_gate) return _inbound.GetValueOrDefault(peerId);
    }
}

internal static class PhysicalFollowProtocol
{
    internal static bool IsValidSessionId(string? value) =>
        value is { Length: 48 } && value.All(static character => char.IsAsciiHexDigit(character));

    internal static bool IsObservationAccepted(
        string expectedSessionId,
        long lastSequence,
        string expectedPublisherId,
        PhysicalFollowObservation notification)
    {
        if (!string.Equals(notification.SessionId, expectedSessionId, StringComparison.Ordinal) ||
            !string.Equals(notification.PublisherDeviceId, expectedPublisherId, StringComparison.Ordinal) ||
            notification.Sequence <= lastSequence || notification.Sequence <= 0)
            return false;
        return true;
    }
}

internal sealed class StableDisplayObservationTracker
{
    private string? _candidate;
    private int _count;

    internal bool Observe(string? deviceId, out string? stableDeviceId)
    {
        stableDeviceId = null;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            Reset();
            return false;
        }
        if (string.Equals(_candidate, deviceId, StringComparison.Ordinal)) _count++;
        else { _candidate = deviceId; _count = 1; }
        if (_count < 2) return false;
        stableDeviceId = _candidate;
        return true;
    }

    internal void Reset()
    {
        _candidate = null;
        _count = 0;
    }
}

internal sealed class FocusTransitionObservationGate(TimeSpan quietPeriod)
{
    private string? _deviceId;
    private long _changedTimestamp;
    private string? _witness;
    private int _witnessSamples;
    private bool _awaitingWitness;

    internal bool ObserveFocus(string deviceId, long now, out bool changed)
    {
        changed = false;
        if (_deviceId is null)
        {
            _deviceId = deviceId;
            return false;
        }
        if (!string.Equals(_deviceId, deviceId, StringComparison.Ordinal))
        {
            _deviceId = deviceId;
            _changedTimestamp = now;
            _witness = null;
            _witnessSamples = 0;
            _awaitingWitness = true;
            changed = true;
        }
        return _changedTimestamp != 0 && Stopwatch.GetElapsedTime(_changedTimestamp, now) < quietPeriod;
    }

    internal bool AcceptObservation(string? observedDeviceId, long now, out bool armed)
    {
        armed = false;
        if (_changedTimestamp != 0 && Stopwatch.GetElapsedTime(_changedTimestamp, now) < quietPeriod)
            return false;
        if (!_awaitingWitness) return true;

        var witness = observedDeviceId is null
            ? "<unreadable>"
            : string.Equals(observedDeviceId, _deviceId, StringComparison.Ordinal)
                ? observedDeviceId
                : null;
        if (witness is null)
        {
            _witness = null;
            _witnessSamples = 0;
            return false;
        }

        if (string.Equals(_witness, witness, StringComparison.Ordinal)) _witnessSamples++;
        else
        {
            _witness = witness;
            _witnessSamples = 1;
        }
        if (_witnessSamples < 2) return false;

        _awaitingWitness = false;
        armed = true;
        return false;
    }
}

internal sealed record OutboundFollowSession(
    string SessionId,
    string PeerId,
    string Fingerprint,
    CancellationToken TrustToken,
    long LastRenewedTimestamp,
    long LastAttemptTimestamp,
    bool ConflictBackoff,
    long LastSequence);

internal sealed record InboundFollowSession(
    string SessionId,
    string PeerId,
    string Fingerprint,
    long LastRenewedTimestamp,
    TimeSpan Lease,
    long LastSequence,
    long LastPublishedTimestamp,
    CancellationToken TrustToken,
    CancellationTokenRegistration TrustRegistration);

internal sealed record PhysicalFollowMappedObservation(string DeviceId, string MonitorId, uint Input);

public sealed record PhysicalFollowSubscriptionCommand(
    string SessionId,
    string SubscriberDeviceId,
    string PublisherDeviceId,
    int LeaseMilliseconds);

public sealed record PhysicalFollowSubscriptionResponse(bool Accepted, bool Created, string Message)
{
    public PhysicalFollowSubscriptionResponse(bool accepted, string message)
        : this(accepted, false, message) { }
}
public sealed record PhysicalFollowObservation(
    string SessionId,
    long Sequence,
    string PublisherDeviceId);
public sealed record PhysicalFollowObservationResponse(bool Accepted, string Message);
