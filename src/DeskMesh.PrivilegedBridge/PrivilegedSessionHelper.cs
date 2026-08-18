using System.Diagnostics;
using System.IO.Pipes;
using LanSwitch.Core.Privileged;
using LanSwitch.Windows.Input;

namespace DeskMesh.PrivilegedBridge;

internal static class PrivilegedSessionHelper
{
    internal static int Run(BridgeCommandLine command)
    {
        if (!Program.IsLocalSystem()) throw new UnauthorizedAccessException("The secure desktop helper must run as LocalSystem.");
        using var injector = new WindowsInputInjector();
        injector.Start();
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                command.PipeName!,
                PipeDirection.InOut,
                PipeOptions.WriteThrough);
            pipe.Connect(5000);
            while (pipe.IsConnected)
            {
                PrivilegedBridgeRequest request;
                try
                {
                    request = PrivilegedBridgeContract.ReadAsync<PrivilegedBridgeRequest>(pipe, CancellationToken.None)
                        .AsTask().GetAwaiter().GetResult();
                }
                catch (EndOfStreamException) { break; }
                catch (IOException) { break; }

                if (request.Operation == PrivilegedBridgeContract.ShutdownOperation) break;
                var response = HandleRequest(injector, request);
                PrivilegedBridgeContract.WriteAsync(pipe, response, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            _ = injector.ReleaseAll();
            injector.Stop();
        }
        return 0;
    }

    internal static PrivilegedBridgeResponse HandleRequest(
        IWindowsInputInjector injector,
        PrivilegedBridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(injector);
        if (request.Version != PrivilegedBridgeContract.Version)
            return Failure("The privileged bridge protocol version is not supported.");

        var desktopName = GetInputDesktopName();
        var secureDesktopActive = IsAllowedDesktop(desktopName, IsConsentUiActive());
        if (request.Operation == PrivilegedBridgeContract.ReleaseOperation)
        {
            var released = injector.ReleaseAll();
            return new PrivilegedBridgeResponse(
                PrivilegedBridgeContract.Version,
                Available: true,
                SecureDesktopActive: secureDesktopActive,
                released.Attempted,
                released.Released,
                desktopName);
        }

        if (request.Operation != PrivilegedBridgeContract.InjectOperation)
            return Failure("The privileged bridge operation is not allowed.", desktopName, secureDesktopActive);
        if (!secureDesktopActive)
            return new PrivilegedBridgeResponse(
                PrivilegedBridgeContract.Version,
                Available: true,
                SecureDesktopActive: false,
                Attempted: 0,
                Succeeded: 0,
                desktopName,
                "The Windows secure desktop is not active.");
        if (request.Events is null ||
            request.Events.Count is <= 0 or > PrivilegedBridgeContract.MaximumEventsPerBatch ||
            request.Events.Any(static input => !IsValid(input)))
            return Failure("The privileged input batch is invalid.", desktopName, secureDesktopActive);

        var attempted = 0;
        var succeeded = 0;
        foreach (var input in request.Events)
        {
            var result = Inject(injector, input);
            attempted += result.Attempted;
            succeeded += result.Sent;
        }
        return new PrivilegedBridgeResponse(
            PrivilegedBridgeContract.Version,
            Available: true,
            SecureDesktopActive: true,
            attempted,
            succeeded,
            desktopName,
            attempted == succeeded ? null : "Windows rejected part of the secure desktop input batch.");
    }

    private static InputInjectionResult Inject(IWindowsInputInjector injector, PrivilegedBridgeInputEvent input) =>
        input.Type switch
        {
            "keyboard" => injector.SendKeyboard(new KeyboardInjection(
                checked((ushort)input.Code),
                checked((ushort)input.Value),
                (input.Flags & 1) != 0 ? InputTransition.Up : InputTransition.Down,
                UseScanCode: input.Value != 0,
                IsExtended: (input.Flags & 2) != 0)),
            "mouse-move" => injector.SendMouseMove(input.Code, input.Value),
            "mouse-position" => injector.SendMousePosition(input.Code, input.Value),
            "mouse-button" => injector.SendMouseButton(new MouseButtonInjection(
                (MouseButton)input.Code,
                input.Value == 1 ? InputTransition.Down : InputTransition.Up)),
            "mouse-wheel" => injector.SendMouseWheel(checked((short)input.Value), input.Code == 1),
            _ => new InputInjectionResult(0, 0, 0)
        };

    private static bool IsValid(PrivilegedBridgeInputEvent input) => input.Type switch
    {
        "keyboard" => input.Code is >= 0 and <= ushort.MaxValue && input.Value is >= 0 and <= ushort.MaxValue &&
                      (input.Flags & ~3) == 0,
        "mouse-move" => input.Flags == 0,
        "mouse-position" => input.Code is >= 0 and <= ushort.MaxValue &&
                            input.Value is >= 0 and <= ushort.MaxValue && input.Flags == 0,
        "mouse-button" => Enum.IsDefined((MouseButton)input.Code) && input.Value is 0 or 1 && input.Flags == 0,
        "mouse-wheel" => input.Code is 0 or 1 && input.Value is >= short.MinValue and <= short.MaxValue && input.Flags == 0,
        _ => false
    };

    internal static string? GetInputDesktopName()
    {
        var desktop = BridgeNativeMethods.OpenInputDesktop(
            0,
            inherit: false,
            BridgeNativeMethods.DesktopReadObjects);
        if (desktop == 0) return null;
        try
        {
            _ = BridgeNativeMethods.GetUserObjectInformation(
                desktop,
                BridgeNativeMethods.UoiName,
                null,
                0,
                out var bytesNeeded);
            if (bytesNeeded <= sizeof(char)) return null;
            var buffer = new char[(bytesNeeded / sizeof(char)) + 1];
            if (!BridgeNativeMethods.GetUserObjectInformation(
                    desktop,
                    BridgeNativeMethods.UoiName,
                    buffer,
                    bytesNeeded,
                    out _))
                return null;
            return new string(buffer).TrimEnd('\0');
        }
        finally
        {
            BridgeNativeMethods.CloseDesktop(desktop);
        }
    }

    internal static bool IsAllowedDesktop(string? desktopName, bool consentUiActive) =>
        consentUiActive && string.Equals(desktopName, "Winlogon", StringComparison.OrdinalIgnoreCase);

    private static bool IsConsentUiActive()
    {
        try
        {
            var currentSession = Process.GetCurrentProcess().SessionId;
            return Process.GetProcessesByName("consent").Any(process =>
            {
                using (process)
                {
                    try { return process.SessionId == currentSession && !process.HasExited; }
                    catch { return false; }
                }
            });
        }
        catch
        {
            return false;
        }
    }

    private static PrivilegedBridgeResponse Failure(
        string error,
        string? desktopName = null,
        bool secureDesktopActive = false) => new(
        PrivilegedBridgeContract.Version,
        Available: false,
        SecureDesktopActive: secureDesktopActive,
        Attempted: 0,
        Succeeded: 0,
        desktopName,
        error);
}
