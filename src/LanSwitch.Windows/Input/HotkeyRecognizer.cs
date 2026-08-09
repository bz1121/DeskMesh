namespace LanSwitch.Windows.Input;

public sealed class HotkeyRecognizer
{
    private readonly object _gate = new();
    private readonly HashSet<ushort> _pressedKeys = [];
    private HotkeyBindings _bindings;
    private readonly bool _ignoreInjected;

    public HotkeyRecognizer(HotkeyBindings? bindings = null, bool ignoreInjected = true)
    {
        _bindings = bindings ?? HotkeyBindings.Default;
        _ignoreInjected = ignoreInjected;
    }

    public HotkeyCommand? Process(KeyboardSignal signal)
    {
        lock (_gate)
        {
            if (_ignoreInjected && signal.IsInjected)
            {
                return null;
            }

            if (signal.Transition == InputTransition.Up)
            {
                _pressedKeys.Remove(signal.VirtualKey);
                return null;
            }

            var isRepeat = !_pressedKeys.Add(signal.VirtualKey);
            if (isRepeat)
            {
                return null;
            }

            var modifiers = GetCurrentModifiers();
            if (Matches(_bindings.EmergencyRelease, signal.VirtualKey, modifiers))
            {
                return HotkeyCommand.EmergencyRelease;
            }

            if (Matches(_bindings.SwitchToPrimary, signal.VirtualKey, modifiers))
            {
                return HotkeyCommand.SwitchToPrimary;
            }

            if (Matches(_bindings.SwitchToSecondary, signal.VirtualKey, modifiers))
            {
                return HotkeyCommand.SwitchToSecondary;
            }

            return null;
        }
    }

    public HotkeyGesture GetGesture(HotkeyCommand command)
    {
        lock (_gate)
        {
            return command switch
            {
                HotkeyCommand.SwitchToPrimary => _bindings.SwitchToPrimary,
                HotkeyCommand.SwitchToSecondary => _bindings.SwitchToSecondary,
                _ => _bindings.EmergencyRelease
            };
        }
    }

    public void UpdateBindings(HotkeyBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        lock (_gate)
        {
            _bindings = bindings;
        }
    }

    public void Reset()
    {
        lock (_gate) _pressedKeys.Clear();
    }

    private KeyModifiers GetCurrentModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (IsAnyPressed(VirtualKeyCodes.Control, VirtualKeyCodes.LeftControl, VirtualKeyCodes.RightControl))
        {
            modifiers |= KeyModifiers.Control;
        }

        if (IsAnyPressed(VirtualKeyCodes.Alt, VirtualKeyCodes.LeftAlt, VirtualKeyCodes.RightAlt))
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (IsAnyPressed(VirtualKeyCodes.Shift, VirtualKeyCodes.LeftShift, VirtualKeyCodes.RightShift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (IsAnyPressed(VirtualKeyCodes.LeftWindows, VirtualKeyCodes.RightWindows))
        {
            modifiers |= KeyModifiers.Windows;
        }

        return modifiers;
    }

    private bool IsAnyPressed(params ushort[] keys) => keys.Any(_pressedKeys.Contains);

    private static bool Matches(HotkeyGesture gesture, ushort key, KeyModifiers modifiers) =>
        gesture.VirtualKey == key && gesture.Modifiers == modifiers;
}
