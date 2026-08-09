namespace LanSwitch.Windows.Input;

public interface IWindowsInputInjector : IDisposable
{
    bool IsStarted { get; }

    int PressedInputCount { get; }

    void Start();

    InputInjectionResult SendKeyboard(KeyboardInjection injection);

    InputInjectionResult SendMouseButton(MouseButtonInjection injection);

    InputInjectionResult SendMouseMove(int deltaX, int deltaY, bool absolute = false);

    InputInjectionResult SendMousePosition(int normalizedX, int normalizedY, bool virtualDesktop = true);

    InputInjectionResult SendMouseWheel(short delta, bool horizontal = false);

    InputReleaseResult ReleaseAll();

    InputReleaseResult Stop();
}
