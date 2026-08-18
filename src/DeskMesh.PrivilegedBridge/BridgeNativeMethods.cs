using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeskMesh.PrivilegedBridge;

internal static class BridgeNativeMethods
{
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ScManagerCreateService = 0x0002;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const uint ServiceStart = 0x0010;
    internal const uint ServiceStop = 0x0020;
    internal const uint ServiceChangeConfig = 0x0002;
    internal const uint Delete = 0x00010000;
    internal const uint ServiceWin32OwnProcess = 0x00000010;
    internal const uint ServiceAutoStart = 0x00000002;
    internal const uint ServiceErrorNormal = 0x00000001;
    internal const uint ServiceControlStop = 0x00000001;
    internal const uint ServiceStopped = 0x00000001;
    internal const uint ServiceStartPending = 0x00000002;
    internal const uint ServiceStopPending = 0x00000003;
    internal const uint ServiceRunning = 0x00000004;
    internal const uint ServiceAcceptStop = 0x00000001;
    internal const int ScStatusProcessInfo = 0;
    internal const int ServiceConfigDescription = 1;
    internal const uint ErrorServiceExists = 1073;
    internal const uint ErrorServiceDoesNotExist = 1060;
    internal const uint ErrorServiceAlreadyRunning = 1056;
    internal const uint TokenAssignPrimary = 0x0001;
    internal const uint TokenDuplicate = 0x0002;
    internal const uint TokenQuery = 0x0008;
    internal const uint TokenAdjustSessionId = 0x0100;
    internal const uint MaximumAllowed = 0x02000000;
    internal const uint CreateNoWindow = 0x08000000;
    internal const int SecurityImpersonation = 2;
    internal const int TokenPrimary = 1;
    internal const int TokenSessionId = 12;
    internal const uint DesktopReadObjects = 0x0001;
    internal const int UoiName = 2;
    internal const uint StillActive = 259;

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    internal delegate void ServiceMainDelegate(uint argumentCount, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint ServiceControlHandlerDelegate(uint control, uint eventType, nint eventData, nint context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] internal string? ServiceName;
        internal ServiceMainDelegate? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusProcess
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
        internal uint ProcessId;
        internal uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ServiceDescription
    {
        [MarshalAs(UnmanagedType.LPWStr)] internal string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        internal uint Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2;
        internal nint Reserved2Pointer;
        internal nint StandardInput;
        internal nint StandardOutput;
        internal nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal nint Process;
        internal nint Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint RegisterServiceCtrlHandlerEx(
        string serviceName,
        ServiceControlHandlerDelegate handler,
        nint context);

    [DllImport("advapi32.dll", EntryPoint = "SetServiceStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetServiceStatus(nint serviceStatusHandle, ref ServiceStatus status);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint OpenServiceControlManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint CreateService(
        nint serviceControlManager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPath,
        string? loadOrderGroup,
        nint tagId,
        string? dependencies,
        string? accountName,
        string? password);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint OpenService(nint serviceControlManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfig(
        nint service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPath,
        string? loadOrderGroup,
        nint tagId,
        string? dependencies,
        string? accountName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceDescription(nint service, int infoLevel, ref ServiceDescription description);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartService(nint service, int argumentCount, nint arguments);

    [DllImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ControlService(nint service, uint control, ref ServiceStatus status);

    [DllImport("advapi32.dll", EntryPoint = "DeleteService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteService(nint service);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatusProcess(
        nint service,
        int infoLevel,
        out ServiceStatusProcess status,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(nint serviceHandle);

    [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", EntryPoint = "DuplicateTokenEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateTokenEx(
        nint existingToken,
        uint desiredAccess,
        nint tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out nint newToken);

    [DllImport("advapi32.dll", EntryPoint = "SetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetTokenSessionId(nint token, int tokenInformationClass, ref uint sessionId, int tokenInformationLength);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessAsUser(
        nint token,
        string? applicationName,
        string commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    internal static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", EntryPoint = "ProcessIdToSessionId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("user32.dll", EntryPoint = "OpenInputDesktop", SetLastError = true)]
    internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformation(
        nint handle,
        int index,
        char[]? information,
        int length,
        out int needed);

    [DllImport("user32.dll", EntryPoint = "CloseDesktop", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(nint desktop);

    internal static Win32Exception CreateError(string operation) =>
        new(Marshal.GetLastWin32Error(), operation);
}
