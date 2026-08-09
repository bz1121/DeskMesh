using System.ComponentModel;
using System.Runtime.InteropServices;
using LanSwitch.Windows.Interop;

namespace LanSwitch.Windows.Input;

public sealed class RawMouseInputEventArgs(int deltaX, int deltaY) : EventArgs
{
    public int DeltaX { get; } = deltaX;

    public int DeltaY { get; } = deltaY;
}

public interface IRawMouseInputMonitor : IDisposable
{
    event EventHandler<RawMouseInputEventArgs>? MouseMoved;

    event EventHandler<RawMouseInputFaultedEventArgs>? Faulted;

    bool IsRunning { get; }

    void Start();

    void Stop();
}

public sealed class RawMouseInputFaultedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

public sealed class WindowsRawMouseInputMonitor : IRawMouseInputMonitor
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly NativeMethods.WindowProcedure _windowProcedure;
    private Thread? _messageThread;
    private ManualResetEventSlim? _startupSignal;
    private Exception? _startupException;
    private nint _windowHandle;
    private uint _messageThreadId;
    private int _isRunning;
    private int _stopRequested;
    private int _disposed;

    public WindowsRawMouseInputMonitor()
    {
        _windowProcedure = WindowProcedure;
    }

    public event EventHandler<RawMouseInputEventArgs>? MouseMoved;

    public event EventHandler<RawMouseInputFaultedEventArgs>? Faulted;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        ManualResetEventSlim startupSignal;
        lock (_sync)
        {
            if (_messageThread is { IsAlive: true }) return;

            _startupSignal?.Dispose();
            _startupSignal = new ManualResetEventSlim();
            startupSignal = _startupSignal;
            _startupException = null;
            Volatile.Write(ref _stopRequested, 0);
            _messageThread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "LanSwitch raw mouse input"
            };
            _messageThread.Start();
        }

        if (!startupSignal.Wait(StartupTimeout))
        {
            RequestStop();
            throw new TimeoutException("The raw mouse input monitor did not start within five seconds.");
        }

        Exception? startupException;
        lock (_sync) startupException = _startupException;
        if (startupException is null) return;

        Stop();
        throw new InvalidOperationException("The raw mouse input monitor could not be started.", startupException);
    }

    public void Stop()
    {
        Thread? thread;
        lock (_sync) thread = _messageThread;
        if (thread is null || !thread.IsAlive) return;

        RequestStop();
        if (ReferenceEquals(Thread.CurrentThread, thread)) return;
        if (!thread.Join(ShutdownTimeout))
            throw new TimeoutException("The raw mouse input monitor did not stop within five seconds.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        lock (_sync)
        {
            _startupSignal?.Dispose();
            _startupSignal = null;
        }
    }

    internal static bool TryGetRelativeDelta(
        ushort flags,
        int lastX,
        int lastY,
        out int deltaX,
        out int deltaY)
    {
        deltaX = 0;
        deltaY = 0;
        if ((flags & NativeMethods.MouseMoveAbsolute) != 0 || (lastX == 0 && lastY == 0))
            return false;

        deltaX = lastX;
        deltaY = lastY;
        return true;
    }

    private void MessageLoop()
    {
        var instanceHandle = NativeMethods.GetModuleHandle(null);
        var className = $"LanSwitch.RawMouse.{Guid.NewGuid():N}";
        var classRegistered = false;
        var rawInputRegistered = false;
        try
        {
            _messageThreadId = NativeMethods.GetCurrentThreadId();
            var windowClass = new NativeMethods.WindowClassEx
            {
                Size = checked((uint)Marshal.SizeOf<NativeMethods.WindowClassEx>()),
                InstanceHandle = instanceHandle,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                ClassName = className
            };
            if (NativeMethods.RegisterClassEx(in windowClass) == 0)
                throw CreateWin32Exception("RegisterClassExW");

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
                throw CreateWin32Exception("CreateWindowExW");

            var devices = new[]
            {
                new NativeMethods.RawInputDevice
                {
                    UsagePage = NativeMethods.HidUsagePageGeneric,
                    Usage = NativeMethods.HidUsageGenericMouse,
                    Flags = NativeMethods.RidevInputSink,
                    TargetWindow = _windowHandle
                }
            };
            if (!NativeMethods.RegisterRawInputDevices(
                    devices,
                    checked((uint)devices.Length),
                    checked((uint)Marshal.SizeOf<NativeMethods.RawInputDevice>())))
            {
                throw CreateWin32Exception("RegisterRawInputDevices");
            }

            rawInputRegistered = true;
            Volatile.Write(ref _isRunning, 1);
            SignalStartup(null);
            if (Volatile.Read(ref _stopRequested) != 0) return;

            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, nint.Zero, 0, 0);
                if (result == 0) break;
                if (result < 0) throw CreateWin32Exception("GetMessageW");
                _ = NativeMethods.TranslateMessage(in message);
                _ = NativeMethods.DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            if (IsRunning) Faulted?.Invoke(this, new RawMouseInputFaultedEventArgs(exception));
            SignalStartup(exception);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            if (rawInputRegistered)
            {
                var remove = new[]
                {
                    new NativeMethods.RawInputDevice
                    {
                        UsagePage = NativeMethods.HidUsagePageGeneric,
                        Usage = NativeMethods.HidUsageGenericMouse,
                        Flags = NativeMethods.RidevRemove,
                        TargetWindow = nint.Zero
                    }
                };
                _ = NativeMethods.RegisterRawInputDevices(
                    remove,
                    checked((uint)remove.Length),
                    checked((uint)Marshal.SizeOf<NativeMethods.RawInputDevice>()));
            }
            if (_windowHandle != nint.Zero)
            {
                _ = NativeMethods.DestroyWindow(_windowHandle);
                _windowHandle = nint.Zero;
            }
            if (classRegistered) _ = NativeMethods.UnregisterClass(className, instanceHandle);
            _messageThreadId = 0;
            SignalStartup(null);
        }
    }

    private nint WindowProcedure(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        if (message == NativeMethods.WmInput)
        {
            var size = checked((uint)Marshal.SizeOf<NativeMethods.RawInput>());
            var read = NativeMethods.GetRawInputData(
                lParam,
                NativeMethods.RidInput,
                out var input,
                ref size,
                checked((uint)Marshal.SizeOf<NativeMethods.RawInputHeader>()));
            if (read != uint.MaxValue && input.Header.Type == NativeMethods.RimTypeMouse &&
                TryGetRelativeDelta(input.Mouse.Flags, input.Mouse.LastX, input.Mouse.LastY, out var x, out var y))
            {
                MouseMoved?.Invoke(this, new RawMouseInputEventArgs(x, y));
            }
        }
        else if (message == NativeMethods.WmClose)
        {
            _ = NativeMethods.DestroyWindow(windowHandle);
            return nint.Zero;
        }
        else if (message == NativeMethods.WmDestroy)
        {
            NativeMethods.PostQuitMessage(0);
            return nint.Zero;
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private void RequestStop()
    {
        Volatile.Write(ref _stopRequested, 1);
        if (_windowHandle != nint.Zero)
            _ = NativeMethods.PostMessage(_windowHandle, NativeMethods.WmClose, 0, 0);
        else if (_messageThreadId != 0)
            _ = NativeMethods.PostThreadMessage(_messageThreadId, NativeMethods.WmQuit, 0, 0);
    }

    private void SignalStartup(Exception? exception)
    {
        lock (_sync)
        {
            _startupException ??= exception;
            _startupSignal?.Set();
        }
    }

    private static Win32Exception CreateWin32Exception(string operation) =>
        new(Marshal.GetLastWin32Error(), $"{operation} failed.");
}
