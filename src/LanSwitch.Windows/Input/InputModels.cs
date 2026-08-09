namespace LanSwitch.Windows.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    Windows = 1 << 3
}

public enum InputTransition
{
    Down,
    Up
}

public enum MouseButton
{
    Left,
    Right,
    Middle,
    X1,
    X2
}

public enum HotkeyCommand
{
    SwitchToPrimary,
    SwitchToSecondary,
    EmergencyRelease
}

public enum InputHookCallbackKind
{
    Keyboard,
    Mouse
}

public enum InputHookFaultReason
{
    CallbackBudgetExceeded,
    CallbackFailed,
    MessageLoopFailed
}

public static class VirtualKeyCodes
{
    public const ushort Shift = 0x10;
    public const ushort Control = 0x11;
    public const ushort Alt = 0x12;
    public const ushort D1 = 0x31;
    public const ushort D2 = 0x32;
    public const ushort F12 = 0x7B;
    public const ushort LeftShift = 0xA0;
    public const ushort RightShift = 0xA1;
    public const ushort LeftControl = 0xA2;
    public const ushort RightControl = 0xA3;
    public const ushort LeftAlt = 0xA4;
    public const ushort RightAlt = 0xA5;
    public const ushort LeftWindows = 0x5B;
    public const ushort RightWindows = 0x5C;
}

public readonly record struct HotkeyGesture(ushort VirtualKey, KeyModifiers Modifiers);

public sealed record HotkeyBindings(
    HotkeyGesture SwitchToPrimary,
    HotkeyGesture SwitchToSecondary,
    HotkeyGesture EmergencyRelease)
{
    public static HotkeyBindings Default { get; } = new(
        new HotkeyGesture(VirtualKeyCodes.D1, KeyModifiers.Control | KeyModifiers.Alt),
        new HotkeyGesture(VirtualKeyCodes.D2, KeyModifiers.Control | KeyModifiers.Alt),
        new HotkeyGesture(
            VirtualKeyCodes.F12,
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift));
}

public readonly record struct KeyboardSignal(
    ushort VirtualKey,
    InputTransition Transition,
    bool IsInjected = false);

public sealed class KeyboardHookEventArgs(
    ushort virtualKey,
    ushort scanCode,
    InputTransition transition,
    bool isExtended,
    bool isInjected,
    bool isLowerIntegrityInjected,
    bool isRepeat,
    uint timestamp) : EventArgs
{
    public ushort VirtualKey { get; } = virtualKey;

    public ushort ScanCode { get; } = scanCode;

    public InputTransition Transition { get; } = transition;

    public bool IsExtended { get; } = isExtended;

    public bool IsInjected { get; } = isInjected;

    public bool IsLowerIntegrityInjected { get; } = isLowerIntegrityInjected;

    public bool IsRepeat { get; } = isRepeat;

    public uint Timestamp { get; } = timestamp;

    public bool Handled { get; set; }
}

public sealed class MouseHookEventArgs(
    int x,
    int y,
    uint message,
    MouseButton? button,
    InputTransition? transition,
    short wheelDelta,
    bool isHorizontalWheel,
    bool isInjected,
    bool isLowerIntegrityInjected,
    uint timestamp) : EventArgs
{
    public int X { get; } = x;

    public int Y { get; } = y;

    public uint Message { get; } = message;

    public MouseButton? Button { get; } = button;

    public InputTransition? Transition { get; } = transition;

    public short WheelDelta { get; } = wheelDelta;

    public bool IsHorizontalWheel { get; } = isHorizontalWheel;

    public bool IsInjected { get; } = isInjected;

    public bool IsLowerIntegrityInjected { get; } = isLowerIntegrityInjected;

    public uint Timestamp { get; } = timestamp;

    public bool Handled { get; set; }
}

public sealed class HotkeyPressedEventArgs(HotkeyCommand command, HotkeyGesture gesture) : EventArgs
{
    public HotkeyCommand Command { get; } = command;

    public HotkeyGesture Gesture { get; } = gesture;
}

public sealed class InputHookFaultedEventArgs(
    InputHookFaultReason reason,
    DateTimeOffset timestampUtc,
    InputHookCallbackKind? callbackKind = null,
    TimeSpan? callbackDuration = null,
    Exception? exception = null) : EventArgs
{
    public InputHookFaultReason Reason { get; } = reason;

    public DateTimeOffset TimestampUtc { get; } = timestampUtc;

    public InputHookCallbackKind? CallbackKind { get; } = callbackKind;

    public TimeSpan? CallbackDuration { get; } = callbackDuration;

    public Exception? Exception { get; } = exception;
}

public sealed record InputHookOptions
{
    public bool CaptureKeyboard { get; init; } = true;

    public bool CaptureMouse { get; init; } = true;

    public bool AllowSuppression { get; init; }

    public bool IgnoreInjectedForHotkeys { get; init; } = true;

    public HotkeyBindings Hotkeys { get; init; } = HotkeyBindings.Default;

    // Windows silently removes low-level hooks when their callback exceeds the OS timeout.
    // Keep this comfortably below that limit and fail open if a consumer exceeds it.
    public TimeSpan MaximumCallbackDuration { get; init; } = TimeSpan.FromMilliseconds(250);
}

public readonly record struct KeyboardInjection(
    ushort VirtualKey,
    ushort ScanCode,
    InputTransition Transition,
    bool UseScanCode = false,
    bool IsExtended = false);

public readonly record struct MouseButtonInjection(MouseButton Button, InputTransition Transition);

public readonly record struct InputInjectionResult(int Attempted, int Sent, int ErrorCode)
{
    public bool Succeeded => Attempted == Sent;
}

public readonly record struct InputReleaseResult(int Attempted, int Released, int ErrorCode, int Remaining)
{
    public bool Succeeded => Attempted == Released && Remaining == 0;
}
