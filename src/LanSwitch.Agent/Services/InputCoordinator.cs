using System.Threading.Channels;
using LanSwitch.Agent.Infrastructure;

namespace LanSwitch.Agent.Services;

public sealed class InputCoordinator
{
    private const int MaximumCoalescedMouseMoves = 64;
    private static readonly TimeSpan MouseMoveFlushInterval = TimeSpan.FromMilliseconds(2);
    private readonly object _gate = new();
    private readonly Channel<InputBatchPacket> _outgoing = Channel.CreateBounded<InputBatchPacket>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private RuntimePeer? _target;
    private long _outgoingEpoch;
    private long _nextOutgoingSequence;
    private (long Epoch, string Source)? _incomingPrepared;
    private (long Epoch, string Source)? _incomingCommitted;
    private (long Epoch, string Source)? _incomingDisplaySwitchClosing;
    private readonly Dictionary<string, long> _incomingEpochHighWater = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecentlyClosedIncomingSession> _recentlyClosedIncoming = new(StringComparer.Ordinal);
    private long _lastIncomingSequence = -1;
    private CancellationTokenSource _outgoingSession = new();
    private long _outgoingGeneration;
    private readonly System.Threading.Timer _mouseMoveFlushTimer;
    private int _pendingMouseDeltaX;
    private int _pendingMouseDeltaY;
    private int _pendingMouseMoveCount;
    private long _pendingMouseTimestamp;

    public InputCoordinator()
    {
        _mouseMoveFlushTimer = new System.Threading.Timer(
            static state => ((InputCoordinator)state!).FlushPendingMouseMove(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public ChannelReader<InputBatchPacket> Outgoing => _outgoing.Reader;
    public RuntimePeer? Target { get { lock (_gate) return _target; } }
    public long OutgoingEpoch { get { lock (_gate) return _outgoingEpoch; } }
    public OutgoingSessionSnapshot GetOutgoingSession()
    {
        lock (_gate) return new OutgoingSessionSnapshot(_outgoingGeneration, _outgoingSession.Token, _target, _outgoingEpoch);
    }
    public event Action<InputBatchPacket>? RemoteBatchReceived;
    public event Action? ReleaseRequested;

    public Task RouteToAsync(RuntimePeer target, long epoch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RouteTo(target, epoch);
        return Task.CompletedTask;
    }

    internal void RouteTo(RuntimePeer target, long epoch)
    {
        lock (_gate)
        {
            _target = target;
            _outgoingEpoch = epoch;
            _nextOutgoingSequence = 0;
            RotateOutgoingSession();
        }
    }

    public Task ReturnLocalAsync(CancellationToken cancellationToken)
    {
        ReturnLocal();
        return Task.CompletedTask;
    }

    public void ReturnLocal()
    {
        lock (_gate) { _target = null; _outgoingEpoch++; RotateOutgoingSession(); }
        ReleaseRequested?.Invoke();
    }

    public Task ReleaseAllAsync(CancellationToken cancellationToken)
    {
        ReleaseRequested?.Invoke();
        return Task.CompletedTask;
    }

    public bool QueueLocalEvents(IReadOnlyList<InputEventPacket> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return false;

        lock (_gate)
        {
            if (_target is null) return false;
            if (TryGetPureMouseMove(events, out var deltaX, out var deltaY, out var timestamp))
            {
                _pendingMouseDeltaX = SaturatingAdd(_pendingMouseDeltaX, deltaX);
                _pendingMouseDeltaY = SaturatingAdd(_pendingMouseDeltaY, deltaY);
                _pendingMouseTimestamp = timestamp;
                _pendingMouseMoveCount++;
                if (_pendingMouseMoveCount == 1)
                {
                    _mouseMoveFlushTimer.Change(MouseMoveFlushInterval, Timeout.InfiniteTimeSpan);
                }
                if (_pendingMouseMoveCount >= MaximumCoalescedMouseMoves)
                {
                    // Try to publish a bounded aggregate. If the queue is full, retain the
                    // relative delta: silently discarding it could make a later click land
                    // at a stale remote coordinate.
                    FlushPendingMouseMoveUnderLock();
                }
                return true;
            }

            // A button/key transition may only follow after every accumulated relative
            // movement has been queued. Otherwise fail local instead of clicking at a
            // stale remote coordinate.
            if (!FlushPendingMouseMoveUnderLock()) return false;
            return TryQueueUnderLock(events);
        }
    }

    private void FlushPendingMouseMove()
    {
        lock (_gate) FlushPendingMouseMoveUnderLock();
    }

    private bool FlushPendingMouseMoveUnderLock()
    {
        _mouseMoveFlushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_pendingMouseMoveCount == 0) return true;

        if (_target is null)
        {
            ClearPendingMouseMoveUnderLock();
            return false;
        }

        var move = new InputEventPacket(
            "mouse-move",
            _pendingMouseDeltaX,
            _pendingMouseDeltaY,
            0,
            _pendingMouseTimestamp);
        if (!TryQueueUnderLock([move]))
        {
            _mouseMoveFlushTimer.Change(MouseMoveFlushInterval, Timeout.InfiniteTimeSpan);
            return false;
        }

        ClearPendingMouseMoveUnderLock();
        return true;
    }

    private bool TryQueueUnderLock(IReadOnlyList<InputEventPacket> events)
    {
        if (_target is null) return false;
        var sequence = checked(_nextOutgoingSequence + 1);
        if (!_outgoing.Writer.TryWrite(new InputBatchPacket(_outgoingEpoch, events, sequence))) return false;
        _nextOutgoingSequence = sequence;
        return true;
    }

    private void ClearPendingMouseMoveUnderLock()
    {
        _pendingMouseDeltaX = 0;
        _pendingMouseDeltaY = 0;
        _pendingMouseMoveCount = 0;
        _pendingMouseTimestamp = 0;
    }

    private static bool TryGetPureMouseMove(
        IReadOnlyList<InputEventPacket> events,
        out int deltaX,
        out int deltaY,
        out long timestamp)
    {
        deltaX = 0;
        deltaY = 0;
        timestamp = 0;
        foreach (var input in events)
        {
            if (input is null || input.Type != "mouse-move" || input.Flags != 0) return false;
            deltaX = SaturatingAdd(deltaX, input.Code);
            deltaY = SaturatingAdd(deltaY, input.Value);
            timestamp = Math.Max(timestamp, input.Timestamp);
        }
        return true;
    }

    private static int SaturatingAdd(int left, int right) =>
        (int)Math.Clamp((long)left + right, int.MinValue, int.MaxValue);

    public bool PrepareIncoming(long epoch, string source)
    {
        var shouldRelease = false;
        lock (_gate)
        {
            PruneRecentlyClosedIncoming();
            if (epoch <= 0 || string.IsNullOrWhiteSpace(source) || _incomingDisplaySwitchClosing is not null)
                return false;
            if (_incomingPrepared == (epoch, source)) return true;
            if (_incomingPrepared is not null) return false;
            if (_incomingEpochHighWater.TryGetValue(source, out var highWater) && epoch <= highWater) return false;
            if (_incomingCommitted is { } committed)
            {
                if (committed.Source != source || epoch <= committed.Epoch) return false;
                _incomingCommitted = null;
                _lastIncomingSequence = -1;
                shouldRelease = true;
            }
            _incomingPrepared = (epoch, source);
            _incomingEpochHighWater[source] = epoch;
        }
        if (shouldRelease) ReleaseRequested?.Invoke();
        return true;
    }

    public bool CommitIncoming(long epoch, string source)
    {
        lock (_gate)
        {
            if (_incomingDisplaySwitchClosing is not null || _incomingPrepared != (epoch, source)) return false;
            _incomingCommitted = (epoch, source);
            _incomingPrepared = null;
            _lastIncomingSequence = -1;
            return true;
        }
    }

    public bool TryBeginIncomingDisplaySwitch(long epoch, string source)
    {
        var shouldRelease = false;
        lock (_gate)
        {
            PruneRecentlyClosedIncoming();
            var session = (Epoch: epoch, Source: source);
            if (_incomingDisplaySwitchClosing is not null) return false;
            if (_incomingEpochHighWater.TryGetValue(source, out var highWater) && highWater > epoch) return false;
            var recentlyClosed = _recentlyClosedIncoming.TryGetValue(source, out var closed) && closed.Epoch == epoch;
            if (_incomingPrepared != session && _incomingCommitted != session && !recentlyClosed) return false;
            if (_incomingPrepared == session) _incomingPrepared = null;
            if (_incomingCommitted == session)
            {
                _incomingCommitted = null;
                _lastIncomingSequence = -1;
            }
            _recentlyClosedIncoming.Remove(source);
            _incomingDisplaySwitchClosing = session;
            shouldRelease = true;
        }
        if (shouldRelease) ReleaseRequested?.Invoke();
        return true;
    }

    public bool IsIncomingPrepared(long epoch, string source)
    {
        lock (_gate) return _incomingDisplaySwitchClosing is null && _incomingPrepared == (epoch, source);
    }

    public bool IsIncomingCommitted(long epoch, string source)
    {
        lock (_gate) return _incomingDisplaySwitchClosing is null && _incomingCommitted == (epoch, source);
    }

    public void CompleteIncomingDisplaySwitch(long epoch, string source)
    {
        lock (_gate)
        {
            if (_incomingDisplaySwitchClosing == (epoch, source)) _incomingDisplaySwitchClosing = null;
        }
    }

    public void AbortIncoming(long epoch, string source)
    {
        var shouldRelease = false;
        lock (_gate)
        {
            if (_incomingPrepared == (epoch, source))
            {
                RememberRecentlyClosed((epoch, source));
                _incomingPrepared = null;
                shouldRelease = true;
            }
            if (_incomingCommitted == (epoch, source))
            {
                RememberRecentlyClosed((epoch, source));
                _incomingCommitted = null;
                _lastIncomingSequence = -1;
                shouldRelease = true;
            }
        }
        if (shouldRelease) ReleaseRequested?.Invoke();
    }

    public void RejectIncomingPreparation(long epoch, string source)
    {
        var shouldRelease = false;
        lock (_gate)
        {
            if (_incomingPrepared == (epoch, source))
            {
                _incomingPrepared = null;
                shouldRelease = true;
            }
        }
        if (shouldRelease) ReleaseRequested?.Invoke();
    }

    public bool AcceptRemoteBatch(string sourceDeviceId, InputBatchPacket packet)
    {
        if (!IsValidBatch(packet))
        {
            RevokeIncoming(sourceDeviceId, packet.Epoch);
            return false;
        }
        var release = false;
        try
        {
            lock (_gate)
            {
                if (_incomingCommitted != (packet.Epoch, sourceDeviceId)) return false;
                if (packet.Sequence <= _lastIncomingSequence) return false;
                if ((_lastIncomingSequence < 0 && packet.Sequence != 1) ||
                    (_lastIncomingSequence >= 0 && packet.Sequence != _lastIncomingSequence + 1))
                {
                    RememberRecentlyClosed((packet.Epoch, sourceDeviceId));
                    _incomingCommitted = null;
                    _lastIncomingSequence = -1;
                    release = true;
                }
                else
                {
                    _lastIncomingSequence = packet.Sequence;
                    // Keep validation and synchronous injection linearized with
                    // display-return closing. Once Closing wins this lock, no
                    // previously validated batch can be injected after ReleaseAll.
                    RemoteBatchReceived?.Invoke(packet);
                }
            }
            if (release) { ReleaseRequested?.Invoke(); return false; }
            return true;
        }
        catch
        {
            FailIncoming();
            throw;
        }
    }

    public void AcceptRemoteRelease() => ReleaseRequested?.Invoke();

    public void RevokeIncoming(string sourceDeviceId, long? epoch = null)
    {
        var shouldRelease = false;
        lock (_gate)
        {
            if (_incomingCommitted is { } committed && committed.Source == sourceDeviceId &&
                (epoch is null || committed.Epoch == epoch.Value))
            {
                RememberRecentlyClosed(committed);
                _incomingCommitted = null;
                _lastIncomingSequence = -1;
                shouldRelease = true;
            }
        }
        if (shouldRelease) ReleaseRequested?.Invoke();
    }

    public void ReleaseIncomingThrough(string sourceDeviceId, long epoch)
    {
        var shouldRelease = false;
        lock (_gate)
        {
            (long Epoch, string Source)? newestClosed = null;
            if (_incomingPrepared is { } prepared && prepared.Source == sourceDeviceId && prepared.Epoch <= epoch)
            {
                newestClosed = prepared;
                _incomingPrepared = null;
                shouldRelease = true;
            }
            if (_incomingCommitted is { } committed && committed.Source == sourceDeviceId && committed.Epoch <= epoch)
            {
                if (newestClosed is null || committed.Epoch > newestClosed.Value.Epoch) newestClosed = committed;
                _incomingCommitted = null;
                _lastIncomingSequence = -1;
                shouldRelease = true;
            }
            if (newestClosed is not null) RememberRecentlyClosed(newestClosed.Value);
        }
        if (shouldRelease) ReleaseRequested?.Invoke();
    }

    public void RevokePeer(string sourceDeviceId)
    {
        var shouldRelease = false;
        lock (_gate)
        {
            if (_incomingPrepared is { } prepared && prepared.Source == sourceDeviceId)
            {
                _incomingPrepared = null;
                shouldRelease = true;
            }
            if (_incomingCommitted is { } committed && committed.Source == sourceDeviceId)
            {
                _incomingCommitted = null;
                _lastIncomingSequence = -1;
                shouldRelease = true;
            }
            _recentlyClosedIncoming.Remove(sourceDeviceId);
        }
        if (shouldRelease) ReleaseRequested?.Invoke();
    }

    public void FailIncoming()
    {
        lock (_gate)
        {
            RememberNewestOpenIncoming();
            _incomingPrepared = null;
            _incomingCommitted = null;
            _lastIncomingSequence = -1;
        }
        ReleaseRequested?.Invoke();
    }

    public void EmergencyReset()
    {
        lock (_gate)
        {
            _target = null;
            _outgoingEpoch++;
            RotateOutgoingSession();
            RememberNewestOpenIncoming();
            _incomingPrepared = null;
            _incomingCommitted = null;
            _lastIncomingSequence = -1;
        }
        ReleaseRequested?.Invoke();
    }

    public OutgoingSessionSnapshot BeginOutgoingDisplayReturn()
    {
        OutgoingSessionSnapshot snapshot;
        lock (_gate)
        {
            snapshot = new OutgoingSessionSnapshot(
                _outgoingGeneration,
                _outgoingSession.Token,
                _target,
                _outgoingEpoch);
            _target = null;
            _outgoingEpoch++;
            _nextOutgoingSequence = 0;
            RememberNewestOpenIncoming();
            _incomingPrepared = null;
            _incomingCommitted = null;
            _lastIncomingSequence = -1;
        }
        ReleaseRequested?.Invoke();
        return snapshot;
    }

    public void CompleteOutgoingDisplayReturn(long generation)
    {
        lock (_gate)
        {
            if (_outgoingGeneration == generation && _target is null) RotateOutgoingSession();
        }
    }

    private void RememberNewestOpenIncoming()
    {
        (long Epoch, string Source)? newest = _incomingPrepared;
        if (_incomingCommitted is { } committed && (newest is null || committed.Epoch > newest.Value.Epoch))
            newest = committed;
        if (newest is not null) RememberRecentlyClosed(newest.Value);
    }

    private void RememberRecentlyClosed((long Epoch, string Source) session)
    {
        _recentlyClosedIncoming[session.Source] = new RecentlyClosedIncomingSession(
            session.Epoch,
            DateTimeOffset.UtcNow.AddSeconds(5));
    }

    private void PruneRecentlyClosedIncoming()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _recentlyClosedIncoming.Where(item => item.Value.ExpiresAt <= now)
                     .Select(static item => item.Key).ToArray())
            _recentlyClosedIncoming.Remove(item);
    }

    private void RotateOutgoingSession()
    {
        _mouseMoveFlushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ClearPendingMouseMoveUnderLock();
        var previous = _outgoingSession;
        _outgoingSession = new CancellationTokenSource();
        _outgoingGeneration++;
        previous.Cancel();
    }

    internal static bool IsValidBatch(InputBatchPacket packet)
    {
        if (packet.Epoch <= 0 || packet.Sequence <= 0 || packet.Events is not { Count: > 0 and <= 64 }) return false;
        foreach (var input in packet.Events)
        {
            if (input is null || input.Timestamp is < 0 or > uint.MaxValue) return false;
            var valid = input.Type switch
            {
                "keyboard" => input.Code is >= 0 and <= ushort.MaxValue &&
                    input.Value is >= 0 and <= ushort.MaxValue && (input.Flags & ~3) == 0,
                "mouse-move" => input.Flags == 0,
                "mouse-button" => input.Code is >= 0 and <= 4 && input.Value is 0 or 1 && input.Flags == 0,
                "mouse-wheel" => input.Code is 0 or 1 && input.Value is >= short.MinValue and <= short.MaxValue &&
                    input.Flags == 0,
                _ => false
            };
            if (!valid) return false;
        }
        return true;
    }
}

public sealed record InputEventPacket(string Type, int Code, int Value, int Flags, long Timestamp);
public sealed record InputBatchPacket(long Epoch, IReadOnlyList<InputEventPacket> Events, long Sequence);
public readonly record struct OutgoingSessionSnapshot(long Generation, CancellationToken Token, RuntimePeer? Target, long Epoch);
internal readonly record struct RecentlyClosedIncomingSession(long Epoch, DateTimeOffset ExpiresAt);
