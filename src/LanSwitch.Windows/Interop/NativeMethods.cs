using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace LanSwitch.Windows.Interop;

internal static class NativeMethods
{
    internal const int ErrorInvalidFunction = 1;
    internal const int ErrorGenFailure = 31;
    internal const int ErrorNotSupported = 50;
    internal const int ErrorOperationAborted = 995;

    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;

    internal const uint WmClose = 0x0010;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmQuit = 0x0012;
    internal const uint WmInput = 0x00FF;
    internal const uint WmClipboardUpdate = 0x031D;
    internal const uint WmClipboardDispatch = 0x8037;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmMButtonDown = 0x0207;
    internal const uint WmMButtonUp = 0x0208;
    internal const uint WmMouseWheel = 0x020A;
    internal const uint WmXButtonDown = 0x020B;
    internal const uint WmXButtonUp = 0x020C;
    internal const uint WmMouseHWheel = 0x020E;

    internal const uint LlkhfExtended = 0x01;
    internal const uint LlkhfLowerIlInjected = 0x02;
    internal const uint LlkhfInjected = 0x10;
    internal const uint LlkhfAltDown = 0x20;
    internal const uint LlkhfUp = 0x80;
    internal const uint LlmhfInjected = 0x01;
    internal const uint LlmhfLowerIlInjected = 0x02;

    internal const uint RidInput = 0x10000003;
    internal const uint RimTypeMouse = 0;
    internal const uint RidevRemove = 0x00000001;
    internal const uint RidevInputSink = 0x00000100;
    internal const ushort HidUsagePageGeneric = 0x01;
    internal const ushort HidUsageGenericMouse = 0x02;
    internal const ushort MouseMoveAbsolute = 0x0001;

    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;
    internal const uint KeyeventfExtendedKey = 0x0001;
    internal const uint KeyeventfKeyUp = 0x0002;
    internal const uint KeyeventfScanCode = 0x0008;
    internal const uint MouseeventfMove = 0x0001;
    internal const uint MouseeventfLeftDown = 0x0002;
    internal const uint MouseeventfLeftUp = 0x0004;
    internal const uint MouseeventfRightDown = 0x0008;
    internal const uint MouseeventfRightUp = 0x0010;
    internal const uint MouseeventfMiddleDown = 0x0020;
    internal const uint MouseeventfMiddleUp = 0x0040;
    internal const uint MouseeventfXDown = 0x0080;
    internal const uint MouseeventfXUp = 0x0100;
    internal const uint MouseeventfWheel = 0x0800;
    internal const uint MouseeventfHWheel = 0x1000;
    internal const uint MouseeventfVirtualDesk = 0x4000;
    internal const uint MouseeventfAbsolute = 0x8000;

    internal const uint EddGetDeviceInterfaceName = 0x00000001;

    internal const byte InputSourceVcpCode = 0x60;

    internal static readonly nint HwndMessage = new(-3);

    internal delegate nint WindowProcedure(nint windowHandle, uint message, nuint wParam, nint lParam);

    internal delegate nint HookProcedure(int code, nuint wParam, nint lParam);

    internal delegate bool MonitorEnumProcedure(
        nint monitorHandle,
        nint deviceContext,
        nint monitorRectangle,
        nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(in WindowClassEx windowClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClass(string className, nint instanceHandle);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parentHandle,
        nint menuHandle,
        nint instanceHandle,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern nint DefWindowProc(nint windowHandle, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    internal static extern int GetMessage(out Message message, nint windowHandle, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(in Message message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern nint DispatchMessage(in Message message);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint windowHandle, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(
        int hookId,
        HookProcedure procedure,
        nint moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hookHandle);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hookHandle, int code, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint deviceCount,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(
        nint rawInputHandle,
        uint command,
        out RawInput data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(
        uint inputCount,
        [In] Input[] inputs,
        int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProcedure callback,
        nint data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevices(
        string? deviceName,
        uint deviceIndex,
        ref DisplayDevice displayDevice,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        nint monitorHandle,
        out uint monitorCount);

    [DllImport("dxva2.dll", EntryPoint = "GetPhysicalMonitorsFromHMONITOR", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPhysicalMonitorsFromHMONITOR(
        nint monitorHandle,
        uint monitorCount,
        [Out] PhysicalMonitor[] physicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyPhysicalMonitor(nint physicalMonitorHandle);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVCPFeatureAndVCPFeatureReply(
        nint physicalMonitorHandle,
        byte vcpCode,
        out MonitorVcpCodeType codeType,
        out uint currentValue,
        out uint maximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetVCPFeature(
        nint physicalMonitorHandle,
        byte vcpCode,
        uint newValue);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint InstanceHandle;
        internal nint IconHandle;
        internal nint CursorHandle;
        internal nint BackgroundBrush;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint WindowHandle;
        internal uint Value;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Point
    {
        internal readonly int X;
        internal readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct KeyboardLowLevelHookData
    {
        internal readonly uint VirtualKeyCode;
        internal readonly uint ScanCode;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MouseLowLevelHookData
    {
        internal readonly Point Point;
        internal readonly uint MouseData;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        internal ushort UsagePage;
        internal ushort Usage;
        internal uint Flags;
        internal nint TargetWindow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct RawInputHeader
    {
        internal readonly uint Type;
        internal readonly uint Size;
        internal readonly nint Device;
        internal readonly nuint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct RawMouse
    {
        internal readonly ushort Flags;
        internal readonly ushort ButtonFlags;
        internal readonly ushort ButtonData;
        internal readonly uint RawButtons;
        internal readonly int LastX;
        internal readonly int LastY;
        internal readonly uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct RawInput
    {
        internal readonly RawInputHeader Header;
        internal readonly RawMouse Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        internal uint Size;
        internal Rectangle MonitorRectangle;
        internal Rectangle WorkRectangle;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        internal uint Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceString;

        internal uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PhysicalMonitor
    {
        internal nint Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Description;
    }

    internal enum MonitorVcpCodeType
    {
        Momentary,
        SetParameter
    }
}
