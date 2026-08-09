using System.Runtime.InteropServices;
using LanSwitch.Windows.Interop;

namespace LanSwitch.Windows.Input;

public sealed class WindowsInputInjector : IWindowsInputInjector
{
    private readonly object _sync = new();
    private readonly PressedInputTracker _tracker;
    private int _started;
    private int _disposed;

    public WindowsInputInjector(PressedInputTracker? tracker = null)
    {
        _tracker = tracker ?? new PressedInputTracker();
    }

    public bool IsStarted => Volatile.Read(ref _started) != 0;

    public int PressedInputCount => _tracker.Count;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _started, 1);
    }

    public InputInjectionResult SendKeyboard(KeyboardInjection injection)
    {
        lock (_sync)
        {
            EnsureStarted();
            var input = CreateKeyboardInput(injection);
            var result = Send([input]);
            if (result.Succeeded)
            {
                _tracker.RecordKeyboard(injection);
            }

            return result;
        }
    }

    public InputInjectionResult SendMouseButton(MouseButtonInjection injection)
    {
        lock (_sync)
        {
            EnsureStarted();
            var input = CreateMouseButtonInput(injection);
            var result = Send([input]);
            if (result.Succeeded)
            {
                _tracker.RecordMouseButton(injection);
            }

            return result;
        }
    }

    public InputInjectionResult SendMouseMove(int deltaX, int deltaY, bool absolute = false)
    {
        lock (_sync)
        {
            EnsureStarted();
            var flags = NativeMethods.MouseeventfMove;
            if (absolute)
            {
                flags |= NativeMethods.MouseeventfAbsolute;
            }

            return Send([
                new NativeMethods.Input
                {
                    Type = NativeMethods.InputMouse,
                    Data = new NativeMethods.InputUnion
                    {
                        Mouse = new NativeMethods.MouseInput
                        {
                            X = deltaX,
                            Y = deltaY,
                            Flags = flags
                        }
                    }
                }
            ]);
        }
    }

    public InputInjectionResult SendMousePosition(int normalizedX, int normalizedY, bool virtualDesktop = true)
    {
        lock (_sync)
        {
            EnsureStarted();
            if (normalizedX is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(normalizedX));
            if (normalizedY is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(normalizedY));
            var flags = NativeMethods.MouseeventfMove | NativeMethods.MouseeventfAbsolute;
            if (virtualDesktop) flags |= NativeMethods.MouseeventfVirtualDesk;
            return Send([
                new NativeMethods.Input
                {
                    Type = NativeMethods.InputMouse,
                    Data = new NativeMethods.InputUnion
                    {
                        Mouse = new NativeMethods.MouseInput
                        {
                            X = normalizedX,
                            Y = normalizedY,
                            Flags = flags
                        }
                    }
                }
            ]);
        }
    }

    public InputInjectionResult SendMouseWheel(short delta, bool horizontal = false)
    {
        lock (_sync)
        {
            EnsureStarted();
            return Send([
                new NativeMethods.Input
                {
                    Type = NativeMethods.InputMouse,
                    Data = new NativeMethods.InputUnion
                    {
                        Mouse = new NativeMethods.MouseInput
                        {
                            MouseData = unchecked((uint)(int)delta),
                            Flags = horizontal
                                ? NativeMethods.MouseeventfHWheel
                                : NativeMethods.MouseeventfWheel
                        }
                    }
                }
            ]);
        }
    }

    public InputReleaseResult ReleaseAll()
    {
        lock (_sync)
        {
            var releasePlan = _tracker.CreateReleasePlan();
            if (releasePlan.Count == 0)
            {
                return new InputReleaseResult(0, 0, 0, 0);
            }

            var nativeInputs = releasePlan.Select(CreateReleaseInput).ToArray();
            var sent = NativeMethods.SendInput(
                checked((uint)nativeInputs.Length),
                nativeInputs,
                Marshal.SizeOf<NativeMethods.Input>());
            var error = sent == nativeInputs.Length ? 0 : Marshal.GetLastWin32Error();
            var confirmed = Math.Min(checked((int)sent), releasePlan.Count);
            for (var index = 0; index < confirmed; index++)
            {
                _tracker.ConfirmReleased(releasePlan[index]);
            }

            return new InputReleaseResult(
                releasePlan.Count,
                confirmed,
                error,
                _tracker.Count);
        }
    }

    public InputReleaseResult Stop()
    {
        lock (_sync)
        {
            var result = ReleaseAll();
            Volatile.Write(ref _started, 0);
            return result;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = Stop();
    }

    private static NativeMethods.Input CreateKeyboardInput(KeyboardInjection injection)
    {
        var flags = injection.Transition == InputTransition.Up
            ? NativeMethods.KeyeventfKeyUp
            : 0;
        if (injection.UseScanCode)
        {
            flags |= NativeMethods.KeyeventfScanCode;
        }

        if (injection.IsExtended)
        {
            flags |= NativeMethods.KeyeventfExtendedKey;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = injection.UseScanCode ? (ushort)0 : injection.VirtualKey,
                    ScanCode = injection.ScanCode,
                    Flags = flags
                }
            }
        };
    }

    private static NativeMethods.Input CreateMouseButtonInput(MouseButtonInjection injection)
    {
        var (flags, data) = GetMouseButtonFlags(injection.Button, injection.Transition);
        return new NativeMethods.Input
        {
            Type = NativeMethods.InputMouse,
            Data = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput
                {
                    MouseData = data,
                    Flags = flags
                }
            }
        };
    }

    private static NativeMethods.Input CreateReleaseInput(TrackedInputRelease release) => release.Kind switch
    {
        TrackedInputKind.Keyboard => CreateKeyboardInput(new KeyboardInjection(
            release.VirtualKey,
            release.ScanCode,
            InputTransition.Up,
            release.UseScanCode,
            release.IsExtended)),
        _ => CreateMouseButtonInput(new MouseButtonInjection(release.MouseButton, InputTransition.Up))
    };

    private static (uint Flags, uint Data) GetMouseButtonFlags(
        MouseButton button,
        InputTransition transition) => (button, transition) switch
    {
        (MouseButton.Left, InputTransition.Down) => (NativeMethods.MouseeventfLeftDown, 0),
        (MouseButton.Left, InputTransition.Up) => (NativeMethods.MouseeventfLeftUp, 0),
        (MouseButton.Right, InputTransition.Down) => (NativeMethods.MouseeventfRightDown, 0),
        (MouseButton.Right, InputTransition.Up) => (NativeMethods.MouseeventfRightUp, 0),
        (MouseButton.Middle, InputTransition.Down) => (NativeMethods.MouseeventfMiddleDown, 0),
        (MouseButton.Middle, InputTransition.Up) => (NativeMethods.MouseeventfMiddleUp, 0),
        (MouseButton.X1, InputTransition.Down) => (NativeMethods.MouseeventfXDown, 1),
        (MouseButton.X1, InputTransition.Up) => (NativeMethods.MouseeventfXUp, 1),
        (MouseButton.X2, InputTransition.Down) => (NativeMethods.MouseeventfXDown, 2),
        (MouseButton.X2, InputTransition.Up) => (NativeMethods.MouseeventfXUp, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    private static InputInjectionResult Send(NativeMethods.Input[] inputs)
    {
        var sent = NativeMethods.SendInput(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<NativeMethods.Input>());
        var error = sent == inputs.Length ? 0 : Marshal.GetLastWin32Error();
        return new InputInjectionResult(inputs.Length, checked((int)sent), error);
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsStarted)
        {
            throw new InvalidOperationException(
                "Input injection is disabled. Call Start explicitly before injecting input.");
        }
    }
}
