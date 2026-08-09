using LanSwitch.Core.Models;

namespace LanSwitch.Core.Focus;

public enum FocusFallbackReason
{
    HeartbeatTimeout,
    PrepareTimeout,
    ProtocolAbort,
    ManualRelease,
    TransportClosed
}

public sealed record FocusFallback(
    string TargetDeviceId,
    long Epoch,
    FocusFallbackReason Reason,
    DateTimeOffset OccurredAtUtc,
    bool ReleaseAllInputs = true,
    bool AttemptDisplayRevert = true);

public sealed class FocusSwitchStateMachine
{
    public static readonly TimeSpan DefaultFailureTimeout = TimeSpan.FromSeconds(2);

    private readonly object _syncRoot = new();
    private readonly string _localDeviceId;
    private readonly TimeSpan _failureTimeout;

    private FocusState _state = FocusState.Local;
    private string? _targetDeviceId;
    private long _epoch;
    private bool _targetReady;
    private DateTimeOffset _updatedAtUtc;
    private DateTimeOffset? _lastHeartbeatUtc;

    public FocusSwitchStateMachine(
        string localDeviceId,
        DateTimeOffset createdAtUtc,
        TimeSpan? failureTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDeviceId);

        _localDeviceId = localDeviceId;
        _updatedAtUtc = createdAtUtc;
        _failureTimeout = failureTimeout ?? DefaultFailureTimeout;

        if (_failureTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(failureTimeout), "Failure timeout must be positive.");
        }
    }

    public TimeSpan FailureTimeout => _failureTimeout;

    public FocusSnapshot Snapshot
    {
        get
        {
            lock (_syncRoot)
            {
                return CreateSnapshot();
            }
        }
    }

    public FocusSnapshot BeginRemoteSwitch(string targetDeviceId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);

        lock (_syncRoot)
        {
            EnsureState(FocusState.Local);
            if (string.Equals(_localDeviceId, targetDeviceId, StringComparison.Ordinal))
            {
                throw new ArgumentException("The target device must differ from the local device.", nameof(targetDeviceId));
            }

            _epoch = checked(_epoch + 1);
            _state = FocusState.PreparingRemote;
            _targetDeviceId = targetDeviceId;
            _targetReady = false;
            _updatedAtUtc = nowUtc;
            _lastHeartbeatUtc = nowUtc;
            return CreateSnapshot();
        }
    }

    public FocusSnapshot MarkTargetReady(long epoch, DateTimeOffset nowUtc)
    {
        lock (_syncRoot)
        {
            EnsureEpoch(epoch);
            EnsureState(FocusState.PreparingRemote);
            EnsureTimestampNotOlder(nowUtc);

            _targetReady = true;
            _updatedAtUtc = nowUtc;
            _lastHeartbeatUtc = nowUtc;
            return CreateSnapshot();
        }
    }

    public FocusSnapshot CommitRemote(long epoch, DateTimeOffset nowUtc)
    {
        lock (_syncRoot)
        {
            EnsureEpoch(epoch);
            EnsureState(FocusState.PreparingRemote);
            EnsureTimestampNotOlder(nowUtc);
            if (!_targetReady)
            {
                throw new InvalidOperationException("The target must be ready before focus can be committed.");
            }

            _state = FocusState.Remote;
            _updatedAtUtc = nowUtc;
            _lastHeartbeatUtc = nowUtc;
            return CreateSnapshot();
        }
    }

    public FocusSnapshot RecordHeartbeat(long epoch, DateTimeOffset nowUtc)
    {
        lock (_syncRoot)
        {
            EnsureEpoch(epoch);
            if (_state is not (FocusState.PreparingRemote or FocusState.Remote or FocusState.Recovering))
            {
                throw new InvalidOperationException($"A heartbeat cannot be recorded while focus is {_state}.");
            }

            EnsureTimestampNotOlder(nowUtc);
            _updatedAtUtc = nowUtc;
            _lastHeartbeatUtc = nowUtc;
            return CreateSnapshot();
        }
    }

    public FocusSnapshot BeginRecovery(long epoch, DateTimeOffset nowUtc)
    {
        lock (_syncRoot)
        {
            EnsureEpoch(epoch);
            if (_state is not (FocusState.PreparingRemote or FocusState.Remote))
            {
                throw new InvalidOperationException($"Recovery cannot begin while focus is {_state}.");
            }

            EnsureTimestampNotOlder(nowUtc);
            _state = FocusState.Recovering;
            _updatedAtUtc = nowUtc;
            return CreateSnapshot();
        }
    }

    public FocusFallback? ReturnToLocal(FocusFallbackReason reason, DateTimeOffset nowUtc)
    {
        lock (_syncRoot)
        {
            if (_state == FocusState.Local || _targetDeviceId is null)
            {
                return null;
            }

            var fallback = new FocusFallback(_targetDeviceId, _epoch, reason, nowUtc);
            SetLocal(nowUtc);
            return fallback;
        }
    }

    public FocusFallback? FailBackIfTimedOut(DateTimeOffset nowUtc)
    {
        lock (_syncRoot)
        {
            if (_state == FocusState.Local || _targetDeviceId is null || _lastHeartbeatUtc is null)
            {
                return null;
            }

            if (nowUtc < _lastHeartbeatUtc.Value || nowUtc - _lastHeartbeatUtc.Value < _failureTimeout)
            {
                return null;
            }

            var reason = _state == FocusState.PreparingRemote
                ? FocusFallbackReason.PrepareTimeout
                : FocusFallbackReason.HeartbeatTimeout;

            var fallback = new FocusFallback(_targetDeviceId, _epoch, reason, nowUtc);
            SetLocal(nowUtc);
            return fallback;
        }
    }

    private void SetLocal(DateTimeOffset nowUtc)
    {
        _state = FocusState.Local;
        _targetDeviceId = null;
        _targetReady = false;
        _updatedAtUtc = nowUtc;
        _lastHeartbeatUtc = null;
    }

    private FocusSnapshot CreateSnapshot()
    {
        return new FocusSnapshot(
            _state,
            _localDeviceId,
            _targetDeviceId,
            _epoch,
            _targetReady,
            _updatedAtUtc,
            _lastHeartbeatUtc);
    }

    private void EnsureEpoch(long epoch)
    {
        if (epoch != _epoch)
        {
            throw new InvalidOperationException($"Epoch {epoch} does not match the current epoch {_epoch}.");
        }
    }

    private void EnsureState(FocusState expected)
    {
        if (_state != expected)
        {
            throw new InvalidOperationException($"Expected focus state {expected}, but the current state is {_state}.");
        }
    }

    private void EnsureTimestampNotOlder(DateTimeOffset timestamp)
    {
        if (timestamp < _updatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Timestamps must be monotonic for a focus session.");
        }
    }
}
