using System.ComponentModel;
using System.Runtime.InteropServices;
using LanSwitch.Windows.Interop;

namespace LanSwitch.Windows.Clipboard;

internal delegate bool ClipboardThreadMessagePoster(
    uint threadId,
    uint message,
    nuint wParam,
    nint lParam);

public sealed class WindowsClipboardUpdateMonitor : IClipboardUpdateMonitor
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly CancellationToken StoppedCancellationToken = new(canceled: true);

    private readonly object _sync = new();
    private readonly object _workSync = new();
    private readonly LinkedList<IClipboardWorkItem> _workItems = new();
    private readonly NativeMethods.WindowProcedure _windowProcedure;
    private readonly ClipboardThreadMessagePoster _postThreadMessage;
    private readonly ClipboardStaDispatcherOptions _dispatcherOptions;
    private readonly bool _registerClipboardListener;

    private Thread? _messageThread;
    private ManualResetEventSlim? _startupSignal;
    private Exception? _startupException;
    private nint _windowHandle;
    private uint _messageThreadId;
    private int _managedMessageThreadId;
    private int _isRunning;
    private int _stopRequested;
    private int _disposed;
    private long _sequence;

    public WindowsClipboardUpdateMonitor(ClipboardStaDispatcherOptions? dispatcherOptions = null)
        : this(dispatcherOptions, registerClipboardListener: true)
    {
    }

    internal WindowsClipboardUpdateMonitor(
        ClipboardStaDispatcherOptions? dispatcherOptions,
        bool registerClipboardListener,
        ClipboardThreadMessagePoster? postThreadMessage = null)
    {
        _dispatcherOptions = dispatcherOptions ?? new ClipboardStaDispatcherOptions();
        _dispatcherOptions.Validate();
        _registerClipboardListener = registerClipboardListener;
        _postThreadMessage = postThreadMessage ?? NativeMethods.PostThreadMessage;
        _windowProcedure = WindowProcedure;
    }

    public event EventHandler<ClipboardUpdatedEventArgs>? ClipboardUpdated;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public Task InvokeAsync(Action operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return InvokeAsync(
            () =>
            {
                operation();
                return true;
            },
            cancellationToken);
    }

    public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsRunning || Volatile.Read(ref _stopRequested) != 0)
        {
            throw new InvalidOperationException("The clipboard STA dispatcher has not been started or is stopping.");
        }

        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _managedMessageThreadId))
        {
            return ExecuteInlineAsync(operation, cancellationToken);
        }

        var workItem = new ClipboardWorkItem<T>(operation, cancellationToken);
        QueueWorkItem(workItem);
        return workItem.Completion;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        ManualResetEventSlim startupSignal;
        lock (_sync)
        {
            if (_messageThread is { IsAlive: true })
            {
                return;
            }

            _startupSignal?.Dispose();
            _startupSignal = new ManualResetEventSlim();
            startupSignal = _startupSignal;
            _startupException = null;
            Volatile.Write(ref _stopRequested, 0);

            _messageThread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "LanSwitch clipboard STA dispatcher"
            };
            _messageThread.SetApartmentState(ApartmentState.STA);
            _messageThread.Start();
        }

        if (!startupSignal.Wait(StartupTimeout))
        {
            RequestStop();
            throw new TimeoutException("The clipboard listener did not start within five seconds.");
        }

        Exception? startupException;
        lock (_sync)
        {
            startupException = _startupException;
        }

        if (startupException is not null)
        {
            Stop();
            throw new InvalidOperationException("The clipboard listener could not be started.", startupException);
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_sync)
        {
            thread = _messageThread;
        }

        if (thread is null || !thread.IsAlive)
        {
            CancelPendingWorkItems();
            return;
        }

        RequestStop();
        if (ReferenceEquals(Thread.CurrentThread, thread))
        {
            return;
        }

        if (!thread.Join(ShutdownTimeout))
        {
            throw new TimeoutException("The clipboard listener did not stop within five seconds.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        lock (_sync)
        {
            _startupSignal?.Dispose();
            _startupSignal = null;
        }
    }

    private void MessageLoop()
    {
        var instanceHandle = NativeMethods.GetModuleHandle(null);
        var className = $"LanSwitch.Clipboard.{Guid.NewGuid():N}";
        var classRegistered = false;
        var listenerRegistered = false;

        try
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                throw new InvalidOperationException("The clipboard dispatcher thread did not enter an STA apartment.");
            }

            Volatile.Write(ref _managedMessageThreadId, Environment.CurrentManagedThreadId);
            _messageThreadId = NativeMethods.GetCurrentThreadId();
            var windowClass = new NativeMethods.WindowClassEx
            {
                Size = checked((uint)Marshal.SizeOf<NativeMethods.WindowClassEx>()),
                InstanceHandle = instanceHandle,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                ClassName = className
            };

            if (NativeMethods.RegisterClassEx(in windowClass) == 0)
            {
                throw CreateWin32Exception("RegisterClassExW");
            }

            classRegistered = true;
            _windowHandle = NativeMethods.CreateWindowEx(
                0,
                className,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                NativeMethods.HwndMessage,
                nint.Zero,
                instanceHandle,
                nint.Zero);

            if (_windowHandle == nint.Zero)
            {
                throw CreateWin32Exception("CreateWindowExW");
            }

            if (_registerClipboardListener && !NativeMethods.AddClipboardFormatListener(_windowHandle))
            {
                throw CreateWin32Exception("AddClipboardFormatListener");
            }

            listenerRegistered = _registerClipboardListener;
            Volatile.Write(ref _isRunning, 1);
            SignalStartup(null);

            if (Volatile.Read(ref _stopRequested) != 0)
            {
                return;
            }

            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, nint.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result < 0)
                {
                    throw CreateWin32Exception("GetMessageW");
                }

                if (message.WindowHandle == nint.Zero && message.Value == NativeMethods.WmClipboardDispatch)
                {
                    ProcessNextWorkItem();
                    continue;
                }

                _ = NativeMethods.TranslateMessage(in message);
                _ = NativeMethods.DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            SignalStartup(exception);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            CancelPendingWorkItems();
            if (listenerRegistered && _windowHandle != nint.Zero)
            {
                _ = NativeMethods.RemoveClipboardFormatListener(_windowHandle);
            }

            if (_windowHandle != nint.Zero)
            {
                _ = NativeMethods.DestroyWindow(_windowHandle);
                _windowHandle = nint.Zero;
            }

            if (classRegistered)
            {
                _ = NativeMethods.UnregisterClass(className, instanceHandle);
            }

            _messageThreadId = 0;
            Volatile.Write(ref _managedMessageThreadId, 0);
            SignalStartup(null);
        }
    }

    private nint WindowProcedure(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        if (ClipboardWindowMessages.IsClipboardUpdate(message))
        {
            var args = new ClipboardUpdatedEventArgs(
                Interlocked.Increment(ref _sequence),
                DateTimeOffset.UtcNow);
            InvokeClipboardUpdated(args);
            return nint.Zero;
        }

        if (message == NativeMethods.WmDestroy)
        {
            NativeMethods.PostQuitMessage(0);
            return nint.Zero;
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private void InvokeClipboardUpdated(ClipboardUpdatedEventArgs args)
    {
        var handlers = ClipboardUpdated;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<ClipboardUpdatedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A consumer callback must not terminate the native message loop.
            }
        }
    }

    private void RequestStop()
    {
        Volatile.Write(ref _stopRequested, 1);
        CancelPendingWorkItems();
        var threadId = Volatile.Read(ref _messageThreadId);
        if (threadId != 0)
        {
            _ = _postThreadMessage(threadId, NativeMethods.WmQuit, 0, nint.Zero);
        }
    }

    private Task<T> ExecuteInlineAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        try
        {
            var result = ClipboardBusyRetryPolicy.Execute(operation, _dispatcherOptions, cancellationToken);
            return Task.FromResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    private void QueueWorkItem(IClipboardWorkItem workItem)
    {
        Exception? postException = null;
        lock (_workSync)
        {
            if (!IsRunning || Volatile.Read(ref _stopRequested) != 0)
            {
                throw new InvalidOperationException("The clipboard STA dispatcher has not been started or is stopping.");
            }

            var threadId = Volatile.Read(ref _messageThreadId);
            if (threadId == 0)
            {
                throw new InvalidOperationException("The clipboard STA dispatcher message queue is unavailable.");
            }

            if (_workItems.Count >= _dispatcherOptions.MaxPendingWorkItems)
            {
                throw new ClipboardDispatcherQueueFullException(_dispatcherOptions.MaxPendingWorkItems);
            }

            var workItemNode = _workItems.AddLast(workItem);
            if (!_postThreadMessage(threadId, NativeMethods.WmClipboardDispatch, 0, nint.Zero))
            {
                _workItems.Remove(workItemNode);
                postException = CreateWin32Exception("PostThreadMessageW");
            }
        }

        if (postException is not null)
        {
            workItem.Fail(postException);
        }
    }

    private void ProcessNextWorkItem()
    {
        IClipboardWorkItem? workItem;
        lock (_workSync)
        {
            workItem = _workItems.First?.Value;
            if (_workItems.First is not null)
            {
                _workItems.RemoveFirst();
            }
        }

        if (workItem is null)
        {
            return;
        }

        if (Volatile.Read(ref _stopRequested) != 0)
        {
            workItem.CancelForStop();
            return;
        }

        workItem.Execute(_dispatcherOptions);
    }

    private void CancelPendingWorkItems()
    {
        List<IClipboardWorkItem>? pendingItems = null;
        lock (_workSync)
        {
            if (_workItems.Count != 0)
            {
                pendingItems = new List<IClipboardWorkItem>(_workItems.Count);
                while (_workItems.First is { } workItemNode)
                {
                    _workItems.RemoveFirst();
                    pendingItems.Add(workItemNode.Value);
                }
            }
        }

        if (pendingItems is null)
        {
            return;
        }

        foreach (var workItem in pendingItems)
        {
            workItem.CancelForStop();
        }
    }

    private void SignalStartup(Exception? exception)
    {
        lock (_sync)
        {
            _startupException ??= exception;
            _startupSignal?.Set();
        }
    }

    private static Win32Exception CreateWin32Exception(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }

    private interface IClipboardWorkItem
    {
        void Execute(ClipboardStaDispatcherOptions options);

        void CancelForStop();

        void Fail(Exception exception);
    }

    private sealed class ClipboardWorkItem<T> : IClipboardWorkItem
    {
        private readonly Func<T> _operation;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private CancellationTokenRegistration _cancellationRegistration;
        private int _registrationReady;
        private int _state;

        internal ClipboardWorkItem(Func<T> operation, CancellationToken cancellationToken)
        {
            _operation = operation;
            _cancellationToken = cancellationToken;

            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.UnsafeRegister(
                    static state => ((ClipboardWorkItem<T>)state!).CancelFromCaller(),
                    this);
                Volatile.Write(ref _registrationReady, 1);
                if (Volatile.Read(ref _state) != 0)
                {
                    ReleaseCancellationRegistration();
                }
            }
        }

        internal Task<T> Completion => _completion.Task;

        public void Execute(ClipboardStaDispatcherOptions options)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                ReleaseCancellationRegistration();
                return;
            }

            ReleaseCancellationRegistration();
            try
            {
                var result = ClipboardBusyRetryPolicy.Execute(_operation, options, _cancellationToken);
                _completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                Volatile.Write(ref _state, 2);
            }
        }

        public void CancelForStop()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                _completion.TrySetCanceled(StoppedCancellationToken);
            }

            ReleaseCancellationRegistration();
        }

        public void Fail(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                _completion.TrySetException(exception);
            }

            ReleaseCancellationRegistration();
        }

        private void CancelFromCaller()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                _completion.TrySetCanceled(_cancellationToken);
            }
        }

        private void ReleaseCancellationRegistration()
        {
            if (Interlocked.Exchange(ref _registrationReady, 0) != 0)
            {
                _ = _cancellationRegistration.Unregister();
            }
        }
    }
}
