namespace LanSwitch.Agent.Services;

/// <summary>
/// Keeps browser remote-desktop input and system-wide focus routing mutually exclusive.
/// </summary>
public sealed class RemoteDesktopOutgoingSessionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<long, CancellationTokenSource> _sessions = [];
    private long _nextSessionId;
    private long _generation;
    private bool _transitionActive;
    private bool _releaseDrainActive;

    public bool HasActiveSessions
    {
        get
        {
            lock (_sync) return _sessions.Count != 0;
        }
    }

    public long CaptureGeneration()
    {
        lock (_sync) return _generation;
    }

    public bool IsGenerationCurrent(long generation)
    {
        lock (_sync) return generation == _generation;
    }

    public SessionLease BeginSession(string targetDeviceId) =>
        BeginSession(targetDeviceId, CaptureGeneration());

    public SessionLease BeginSession(string targetDeviceId, long expectedGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
        lock (_sync)
        {
            if (expectedGeneration != _generation)
                throw new InvalidOperationException(
                    "远程桌面请求已被回本机操作取代，请重新读取屏幕后再连接。");
            if (_transitionActive || _releaseDrainActive)
                throw new InvalidOperationException(
                    "系统级键鼠切换正在进行，请等待完成后再打开远程桌面。");

            var id = ++_nextSessionId;
            var cancellation = new CancellationTokenSource();
            _sessions.Add(id, cancellation);
            return new SessionLease(this, id, targetDeviceId, cancellation);
        }
    }

    public IDisposable? TryAcquireFocusTransition()
    {
        lock (_sync)
        {
            if (_transitionActive || _releaseDrainActive || _sessions.Count != 0) return null;
            _transitionActive = true;
            return new TransitionLease(this);
        }
    }

    public bool CancelAllAndAdvanceGeneration()
    {
        CancellationTokenSource[] sessions;
        lock (_sync)
        {
            _generation = checked(_generation + 1);
            _releaseDrainActive = _sessions.Count != 0;
            if (_sessions.Count == 0) return false;
            sessions = _sessions.Values.ToArray();
        }

        foreach (var session in sessions)
        {
            try { session.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return true;
    }

    private void EndSession(long id, CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            _sessions.Remove(id);
            if (_sessions.Count == 0) _releaseDrainActive = false;
        }
        cancellation.Dispose();
    }

    private void EndTransition()
    {
        lock (_sync) _transitionActive = false;
    }

    public sealed class SessionLease : IDisposable
    {
        private RemoteDesktopOutgoingSessionRegistry? _owner;
        private readonly long _id;
        private readonly CancellationTokenSource _cancellation;

        internal SessionLease(
            RemoteDesktopOutgoingSessionRegistry owner,
            long id,
            string targetDeviceId,
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            _id = id;
            TargetDeviceId = targetDeviceId;
            _cancellation = cancellation;
        }

        public string TargetDeviceId { get; }
        public CancellationToken CancellationToken => _cancellation.Token;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndSession(_id, _cancellation);
        }
    }

    private sealed class TransitionLease(RemoteDesktopOutgoingSessionRegistry owner) : IDisposable
    {
        private RemoteDesktopOutgoingSessionRegistry? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndTransition();
    }
}
