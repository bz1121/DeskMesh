namespace LanSwitch.Windows.Input;

public interface IInputHook : IDisposable
{
    event EventHandler<KeyboardHookEventArgs>? KeyboardInput;

    event EventHandler<MouseHookEventArgs>? MouseInput;

    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    event EventHandler<InputHookFaultedEventArgs>? Faulted;

    bool IsRunning { get; }

    bool IsHealthy { get; }

    void Start();

    void UpdateHotkeys(HotkeyBindings hotkeys);

    void Stop();
}
