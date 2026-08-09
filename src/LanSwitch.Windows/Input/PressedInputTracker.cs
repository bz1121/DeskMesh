namespace LanSwitch.Windows.Input;

public enum TrackedInputKind
{
    Keyboard,
    MouseButton
}

public readonly record struct TrackedInputRelease(
    TrackedInputKind Kind,
    ushort VirtualKey,
    ushort ScanCode,
    bool UseScanCode,
    bool IsExtended,
    MouseButton MouseButton)
{
    public static TrackedInputRelease ForKeyboard(KeyboardInjection injection) => new(
        TrackedInputKind.Keyboard,
        injection.VirtualKey,
        injection.ScanCode,
        injection.UseScanCode,
        injection.IsExtended,
        default);

    public static TrackedInputRelease ForMouse(MouseButton button) => new(
        TrackedInputKind.MouseButton,
        0,
        0,
        false,
        false,
        button);
}

public sealed class PressedInputTracker
{
    private readonly object _sync = new();
    private readonly List<TrackedInputRelease> _pressOrder = [];

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _pressOrder.Count;
            }
        }
    }

    public void RecordKeyboard(KeyboardInjection injection)
    {
        var release = TrackedInputRelease.ForKeyboard(injection);
        lock (_sync)
        {
            if (injection.Transition == InputTransition.Down)
            {
                if (!_pressOrder.Contains(release))
                {
                    _pressOrder.Add(release);
                }
            }
            else
            {
                _pressOrder.Remove(release);
            }
        }
    }

    public void RecordMouseButton(MouseButtonInjection injection)
    {
        var release = TrackedInputRelease.ForMouse(injection.Button);
        lock (_sync)
        {
            if (injection.Transition == InputTransition.Down)
            {
                if (!_pressOrder.Contains(release))
                {
                    _pressOrder.Add(release);
                }
            }
            else
            {
                _pressOrder.Remove(release);
            }
        }
    }

    public IReadOnlyList<TrackedInputRelease> CreateReleasePlan()
    {
        lock (_sync)
        {
            var releases = _pressOrder.ToArray();
            Array.Reverse(releases);
            return releases;
        }
    }

    public bool ConfirmReleased(TrackedInputRelease release)
    {
        lock (_sync)
        {
            return _pressOrder.Remove(release);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _pressOrder.Clear();
        }
    }
}
