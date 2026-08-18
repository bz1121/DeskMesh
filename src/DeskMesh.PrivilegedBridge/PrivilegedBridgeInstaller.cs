using System.Runtime.InteropServices;
using LanSwitch.Core.Privileged;

namespace DeskMesh.PrivilegedBridge;

internal static class PrivilegedBridgeInstaller
{
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const int ErrorServiceNotActive = 1062;
    private static readonly TimeSpan ServiceTransitionTimeout = TimeSpan.FromSeconds(15);

    internal static int Install(BridgeCommandLine command)
    {
        if (!Program.IsAdministrator()) throw new UnauthorizedAccessException("Administrator approval is required.");
        var ownerSid = command.OwnerSid!;
        var instanceName = command.InstanceName!;
        var serviceName = PrivilegedBridgeContract.GetServiceName(ownerSid, instanceName);
        var targetPath = GetInstalledExecutablePath(ownerSid, instanceName);
        var sourcePath = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The bridge executable path is unavailable.");

        var manager = BridgeNativeMethods.OpenServiceControlManager(
            null,
            null,
            BridgeNativeMethods.ScManagerConnect | BridgeNativeMethods.ScManagerCreateService);
        if (manager == 0) throw BridgeNativeMethods.CreateError("OpenSCManagerW");
        try
        {
            var access = BridgeNativeMethods.ServiceQueryStatus |
                         BridgeNativeMethods.ServiceStart |
                         BridgeNativeMethods.ServiceStop |
                         BridgeNativeMethods.ServiceChangeConfig |
                         BridgeNativeMethods.Delete;
            var service = BridgeNativeMethods.OpenService(manager, serviceName, access);
            if (service != 0)
            {
                try { StopAndWait(service); }
                finally { BridgeNativeMethods.CloseServiceHandle(service); }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var stagingPath = targetPath + ".new";
            File.Copy(sourcePath, stagingPath, overwrite: true);
            File.Move(stagingPath, targetPath, overwrite: true);

            var binaryPath = BuildServiceCommandLine(targetPath, ownerSid, instanceName);
            service = BridgeNativeMethods.OpenService(manager, serviceName, access);
            if (service == 0)
            {
                service = BridgeNativeMethods.CreateService(
                    manager,
                    serviceName,
                    "DeskMesh UAC secure desktop bridge",
                    access,
                    BridgeNativeMethods.ServiceWin32OwnProcess,
                    BridgeNativeMethods.ServiceAutoStart,
                    BridgeNativeMethods.ServiceErrorNormal,
                    binaryPath,
                    null,
                    0,
                    null,
                    null,
                    null);
                if (service == 0) throw BridgeNativeMethods.CreateError("CreateServiceW");
            }
            else if (!BridgeNativeMethods.ChangeServiceConfig(
                         service,
                         ServiceNoChange,
                         BridgeNativeMethods.ServiceAutoStart,
                         ServiceNoChange,
                         binaryPath,
                         null,
                         0,
                         null,
                         null,
                         null,
                         "DeskMesh UAC secure desktop bridge"))
            {
                BridgeNativeMethods.CloseServiceHandle(service);
                throw BridgeNativeMethods.CreateError("ChangeServiceConfigW");
            }

            try
            {
                var description = new BridgeNativeMethods.ServiceDescription
                {
                    Description = "Relays authenticated DeskMesh input to the Windows UAC secure desktop."
                };
                _ = BridgeNativeMethods.ChangeServiceDescription(
                    service,
                    BridgeNativeMethods.ServiceConfigDescription,
                    ref description);
                if (!BridgeNativeMethods.StartService(service, 0, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != BridgeNativeMethods.ErrorServiceAlreadyRunning)
                        throw new System.ComponentModel.Win32Exception(error, "StartServiceW");
                }
                WaitForState(service, BridgeNativeMethods.ServiceRunning, ServiceTransitionTimeout);
            }
            finally
            {
                BridgeNativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            BridgeNativeMethods.CloseServiceHandle(manager);
        }

        return 0;
    }

    internal static int Uninstall(BridgeCommandLine command)
    {
        if (!Program.IsAdministrator()) throw new UnauthorizedAccessException("Administrator approval is required.");
        var serviceName = PrivilegedBridgeContract.GetServiceName(command.OwnerSid!, command.InstanceName!);
        var manager = BridgeNativeMethods.OpenServiceControlManager(null, null, BridgeNativeMethods.ScManagerConnect);
        if (manager == 0) throw BridgeNativeMethods.CreateError("OpenSCManagerW");
        try
        {
            var service = BridgeNativeMethods.OpenService(
                manager,
                serviceName,
                BridgeNativeMethods.ServiceQueryStatus | BridgeNativeMethods.ServiceStop | BridgeNativeMethods.Delete);
            if (service == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == BridgeNativeMethods.ErrorServiceDoesNotExist) return 0;
                throw new System.ComponentModel.Win32Exception(error, "OpenServiceW");
            }

            try
            {
                StopAndWait(service);
                if (!BridgeNativeMethods.DeleteService(service))
                    throw BridgeNativeMethods.CreateError("DeleteService");
            }
            finally
            {
                BridgeNativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            BridgeNativeMethods.CloseServiceHandle(manager);
        }

        var installedPath = GetInstalledExecutablePath(command.OwnerSid!, command.InstanceName!);
        if (!string.Equals(installedPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(installedPath))
            File.Delete(installedPath);
        return 0;
    }

    internal static string GetInstalledExecutablePath(string ownerSid, string instanceName)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            throw new InvalidOperationException("Program Files is unavailable.");
        var suffix = PrivilegedBridgeContract.GetInstanceSuffix(ownerSid, instanceName);
        return Path.Combine(
            programFiles,
            "DeskMesh",
            "PrivilegedBridge",
            suffix,
            "DeskMesh.PrivilegedBridge.exe");
    }

    internal static string BuildServiceCommandLine(string executablePath, string ownerSid, string instanceName) =>
        $"\"{executablePath}\" --service --owner-sid \"{ownerSid}\" --instance \"{instanceName}\"";

    private static void StopAndWait(nint service)
    {
        if (!TryQuery(service, out var status) || status.CurrentState == BridgeNativeMethods.ServiceStopped) return;
        var controlStatus = new BridgeNativeMethods.ServiceStatus();
        if (!BridgeNativeMethods.ControlService(service, BridgeNativeMethods.ServiceControlStop, ref controlStatus))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceNotActive)
                throw new System.ComponentModel.Win32Exception(error, "ControlService(STOP)");
        }
        WaitForState(service, BridgeNativeMethods.ServiceStopped, ServiceTransitionTimeout);
    }

    private static void WaitForState(nint service, uint expectedState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TryQuery(service, out var status) && status.CurrentState == expectedState) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException("The DeskMesh privileged bridge service did not reach the expected state.");
    }

    private static bool TryQuery(nint service, out BridgeNativeMethods.ServiceStatusProcess status) =>
        BridgeNativeMethods.QueryServiceStatusProcess(
            service,
            BridgeNativeMethods.ScStatusProcessInfo,
            out status,
            Marshal.SizeOf<BridgeNativeMethods.ServiceStatusProcess>(),
            out _);
}
