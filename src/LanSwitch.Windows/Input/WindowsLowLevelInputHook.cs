using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LanSwitch.Windows.Interop;

namespace LanSwitch.Windows.Input;

public sealed class WindowsLowLevelInputHook : IInputHook
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly InputHookOptions _options;
    private readonly HotkeyRecognizer _hotkeyRecognizer;
    private readonly HashSet<ushort> _observedKeys = [];
    private readonly HotkeySuppressionState _hotkeySuppression = new();
    private readonly NativeMethods.HookProcedure _keyboardProcedure;
    private readonly NativeMethods.HookProcedure _mouseProcedure;

    private Thread? _messageThread;
    private ManualResetEventSlim? _startupSignal;
    private Exception? _startupException;
    private uint _messageThreadId;
    private nint _keyboardHook;
    private nint _mouseHook;
    private int _isRunning;
    private int _stopRequested;
    private int _faultReported;
    private int _disposed;

    public WindowsLowLevelInputHook(InputHookOptions? options = null)
    {
        _options = options ?? new InputHookOptions();
        if (!_options.CaptureKeyboard && !_options.CaptureMouse)
        {
            throw new ArgumentException("At least one input hook must be enabled.", nameof(options));
        }

        InputHookCallbackBudget.Validate(_options.MaximumCallbackDuration);

        _hotkeyRecognizer = new HotkeyRecognizer(
            _options.Hotkeys,
            _options.IgnoreInjectedForHotkeys);
        _keyboardProcedure = KeyboardHookProcedure;
        _mouseProcedure = MouseHookProcedure;
    }

    public event EventHandler<KeyboardHookEventArgs>? KeyboardInput;

    public event EventHandler<MouseHookEventArgs>? MouseInput;

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public event EventHandler<InputHookFaultedEventArgs>? Faulted;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public bool IsHealthy => IsRunning && Volatile.Read(ref _faultReported) == 0;

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
            Volatile.Write(ref _faultReported, 0);

            _messageThread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "LanSwitch low-level input hooks"
            };
            _messageThread.Start();
        }

        if (!startupSignal.Wait(StartupTimeout))
        {
            RequestStop();
            throw new TimeoutException("The low-level input hooks did not start within five seconds.");
        }

        Exception? startupException;
        lock (_sync)
        {
            startupException = _startupException;
        }

        if (startupException is not null)
        {
            Stop();
            throw new InvalidOperationException("The low-level input hooks could not be started.", startupException);
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
            return;
        }

        RequestStop();
        if (ReferenceEquals(Thread.CurrentThread, thread))
        {
            return;
        }

        if (!thread.Join(ShutdownTimeout))
        {
            throw new TimeoutException("The low-level input hooks did not stop within five seconds.");
        }
    }

    public void UpdateHotkeys(HotkeyBindings hotkeys)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _hotkeyRecognizer.UpdateBindings(hotkeys);
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
        try
        {
            _messageThreadId = NativeMethods.GetCurrentThreadId();
            var moduleHandle = NativeMethods.GetModuleHandle(null);

            if (_options.CaptureKeyboard)
            {
                _keyboardHook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WhKeyboardLl,
                    _keyboardProcedure,
                    moduleHandle,
                    0);
                if (_keyboardHook == nint.Zero)
                {
                    throw CreateWin32Exception("SetWindowsHookExW(WH_KEYBOARD_LL)");
                }
            }

            if (_options.CaptureMouse)
            {
                _mouseHook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WhMouseLl,
                    _mouseProcedure,
                    moduleHandle,
                    0);
                if (_mouseHook == nint.Zero)
                {
                    throw CreateWin32Exception("SetWindowsHookExW(WH_MOUSE_LL)");
                }
            }

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

                _ = NativeMethods.TranslateMessage(in message);
                _ = NativeMethods.DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            if (IsRunning)
            {
                ReportFault(new InputHookFaultedEventArgs(
                    InputHookFaultReason.MessageLoopFailed,
                    DateTimeOffset.UtcNow,
                    exception: exception));
            }

            SignalStartup(exception);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            if (_mouseHook != nint.Zero)
            {
                _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = nint.Zero;
            }

            if (_keyboardHook != nint.Zero)
            {
                _ = NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = nint.Zero;
            }

            _hotkeyRecognizer.Reset();
            _observedKeys.Clear();
            _hotkeySuppression.Reset();
            _messageThreadId = 0;
            SignalStartup(null);
        }
    }

    private nint KeyboardHookProcedure(int code, nuint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        var callbackStarted = Stopwatch.GetTimestamp();
        try
        {
            var transition = InputMessageClassifier.ClassifyKeyboard(checked((uint)wParam));
            if (transition is null)
            {
                return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
            }

            var data = Marshal.PtrToStructure<NativeMethods.KeyboardLowLevelHookData>(lParam);
            var virtualKey = checked((ushort)data.VirtualKeyCode);
            var isInjected = (data.Flags & NativeMethods.LlkhfInjected) != 0;
            var isRepeat = false;

            if (transition == InputTransition.Down)
            {
                isRepeat = !_observedKeys.Add(virtualKey);
            }
            else
            {
                _observedKeys.Remove(virtualKey);
            }

            var command = _hotkeyRecognizer.Process(new KeyboardSignal(virtualKey, transition.Value, isInjected));
            if (command is not null)
            {
                var gesture = _hotkeyRecognizer.GetGesture(command.Value);
                if (!InvokeHandlersWithinBudget(
                        HotkeyPressed,
                        new HotkeyPressedEventArgs(command.Value, gesture),
                        callbackStarted))
                {
                    return CompleteHookCallback(
                        code,
                        wParam,
                        lParam,
                        InputHookCallbackKind.Keyboard,
                        callbackStarted,
                        suppressionRequested: false);
                }
            }

            if (_options.AllowSuppression &&
                _hotkeySuppression.ShouldSuppress(virtualKey, transition.Value, command is not null))
            {
                return CompleteHookCallback(
                    code,
                    wParam,
                    lParam,
                    InputHookCallbackKind.Keyboard,
                    callbackStarted,
                    suppressionRequested: true);
            }

            var args = new KeyboardHookEventArgs(
                virtualKey,
                checked((ushort)data.ScanCode),
                transition.Value,
                (data.Flags & NativeMethods.LlkhfExtended) != 0,
                isInjected,
                (data.Flags & NativeMethods.LlkhfLowerIlInjected) != 0,
                isRepeat,
                data.Time);
            _ = InvokeHandlersWithinBudget(KeyboardInput, args, callbackStarted);

            return CompleteHookCallback(
                code,
                wParam,
                lParam,
                InputHookCallbackKind.Keyboard,
                callbackStarted,
                _options.AllowSuppression && args.Handled);
        }
        catch (Exception exception)
        {
            FailOpenAfterCallbackFailure(
                InputHookCallbackKind.Keyboard,
                callbackStarted,
                exception);
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }
    }

    internal sealed class HotkeySuppressionState
    {
        private readonly HashSet<ushort> _keys = [];

        internal bool ShouldSuppress(ushort virtualKey, InputTransition transition, bool recognized)
        {
            if (recognized && transition == InputTransition.Down)
                _keys.Add(virtualKey);

            if (!_keys.Contains(virtualKey)) return false;
            if (transition == InputTransition.Up) _keys.Remove(virtualKey);
            return true;
        }

        internal void Reset() => _keys.Clear();
    }

    private nint MouseHookProcedure(int code, nuint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        var callbackStarted = Stopwatch.GetTimestamp();
        try
        {
            var message = checked((uint)wParam);
            var data = Marshal.PtrToStructure<NativeMethods.MouseLowLevelHookData>(lParam);
            var classification = InputMessageClassifier.ClassifyMouse(message, data.MouseData);
            var args = new MouseHookEventArgs(
                data.Point.X,
                data.Point.Y,
                message,
                classification.Button,
                classification.Transition,
                classification.IsWheel ? InputMessageClassifier.GetWheelDelta(data.MouseData) : (short)0,
                classification.IsHorizontalWheel,
                (data.Flags & NativeMethods.LlmhfInjected) != 0,
                (data.Flags & NativeMethods.LlmhfLowerIlInjected) != 0,
                data.Time);
            _ = InvokeHandlersWithinBudget(MouseInput, args, callbackStarted);

            return CompleteHookCallback(
                code,
                wParam,
                lParam,
                InputHookCallbackKind.Mouse,
                callbackStarted,
                _options.AllowSuppression && args.Handled);
        }
        catch (Exception exception)
        {
            FailOpenAfterCallbackFailure(
                InputHookCallbackKind.Mouse,
                callbackStarted,
                exception);
            return NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }
    }

    private nint CompleteHookCallback(
        int code,
        nuint wParam,
        nint lParam,
        InputHookCallbackKind callbackKind,
        long callbackStarted,
        bool suppressionRequested)
    {
        var elapsed = Stopwatch.GetElapsedTime(callbackStarted);
        if (InputHookCallbackBudget.IsExceeded(elapsed, _options.MaximumCallbackDuration))
        {
            ReportFault(new InputHookFaultedEventArgs(
                InputHookFaultReason.CallbackBudgetExceeded,
                DateTimeOffset.UtcNow,
                callbackKind,
                elapsed));
            RequestStop();
            suppressionRequested = false;
        }

        return suppressionRequested
            ? new nint(1)
            : NativeMethods.CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private void FailOpenAfterCallbackFailure(
        InputHookCallbackKind callbackKind,
        long callbackStarted,
        Exception exception)
    {
        ReportFault(new InputHookFaultedEventArgs(
            InputHookFaultReason.CallbackFailed,
            DateTimeOffset.UtcNow,
            callbackKind,
            Stopwatch.GetElapsedTime(callbackStarted),
            exception));
        RequestStop();
    }

    private void ReportFault(InputHookFaultedEventArgs args)
    {
        if (Interlocked.CompareExchange(ref _faultReported, 1, 0) != 0)
        {
            return;
        }

        var handlers = Faulted;
        if (handlers is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (EventHandler<InputHookFaultedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch
                {
                    // Health observers run away from the native hook thread and are isolated.
                }
            }
        });
    }

    private void RequestStop()
    {
        Volatile.Write(ref _stopRequested, 1);
        var threadId = Volatile.Read(ref _messageThreadId);
        if (threadId != 0)
        {
            _ = NativeMethods.PostThreadMessage(threadId, NativeMethods.WmQuit, 0, nint.Zero);
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

    private bool InvokeHandlersWithinBudget<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs args,
        long callbackStarted)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return true;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            if (InputHookCallbackBudget.IsExceeded(
                    Stopwatch.GetElapsedTime(callbackStarted),
                    _options.MaximumCallbackDuration))
            {
                return false;
            }

            try
            {
                handler(this, args);
            }
            catch
            {
                // Hook callbacks have a strict timeout; consumer failures cannot escape them.
            }
        }

        return !InputHookCallbackBudget.IsExceeded(
            Stopwatch.GetElapsedTime(callbackStarted),
            _options.MaximumCallbackDuration);
    }

    private static Win32Exception CreateWin32Exception(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }
}
